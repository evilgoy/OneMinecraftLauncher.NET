﻿using GoodTimeStudio.OneMinecraftLauncher.Core.Models;
using GoodTimeStudio.OneMinecraftLauncher.Core.Models.Minecraft;
using GoodTimeStudio.OneMinecraftLauncher.UWP.Models;
using GoodTimeStudio.OneMinecraftLauncher.UWP.News;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using System.Timers;
using Windows.ApplicationModel;
using Windows.ApplicationModel.AppService;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

// https://go.microsoft.com/fwlink/?LinkId=234238 上介绍了“空白页”项模板

namespace GoodTimeStudio.OneMinecraftLauncher.UWP.View
{
    /// <summary>
    /// 可用于自身或导航至 Frame 内部的空白页。
    /// </summary>
    public sealed partial class StartPage : Page
    {
        public StartPageViewModel ViewModel;
        public INewsSource NewsSource;

        private MsgDialog _msgDialog;

        public StartPage()
        {
            this.InitializeComponent();
            OptListView.OptListViewModel = CoreManager.OptListViewModel ?? new LaunchOptionListViewModel();
            ViewModel = new StartPageViewModel(OptListView.OptListViewModel);
            _msgDialog = new MsgDialog();
            NewsSource = new OfficialNews();
            GetNews();
        }

        private async void GetNews()
        {
            ViewModel.NewsList = await NewsSource.GetNewsAsync();

            if (ViewModel.NewsList != null && ViewModel.NewsList.Count > 1)
            {
                Timer timer = new Timer(5000);
                timer.Elapsed += NewsTimer_Elapsed;
                timer.Start();
            }
        }

        //Auto flip
        private async void NewsTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                if (NewsFlipView.SelectedIndex < ViewModel.NewsList.Count - 1)
                {
                    NewsFlipView.SelectedIndex++;
                }
                else
                {
                    for (; NewsFlipView.SelectedIndex > 0; NewsFlipView.SelectedIndex--) { }
                }
            });
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            //WebView.Navigate(new Uri("https://minecraft.net"));
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
        }

        private async void Button_Launch_Click(object sender, RoutedEventArgs e)
        {
            LaunchOption option = ViewModel.ListModel.SelectedOption;
            if (option == null)
            {
                return;
            }

            option.isReady = false;
            option.lastUsed = DateTime.Now;
            await Launch(option);
            option.isReady = true;
            await CoreManager.SaveOptionList(ViewModel.ListModel.OptionList);
        }

        private async Task Launch(LaunchOption option)
        {
            //Check connection to launch agent
            if (AppServiceManager.appServiceConnection == null)
            {
                return;
            }

            List<MinecraftAssembly> missingLibs = null;  //include missing natives
            List<MinecraftAsset> missingAssets = new List<MinecraftAsset>();

            #region Libraries and natives check
            ValueSet valueSet = new ValueSet();
            valueSet["type"] = "librariesCheck";
            valueSet["version"] = option.versionId;
            AppServiceResponse response = await AppServiceManager.appServiceConnection.SendMessageAsync(valueSet);

            string responseJson = response.Message["value"].ToString();
            try
            {
                missingLibs = JsonConvert.DeserializeObject<List<MinecraftAssembly>>(responseJson);
            }
            catch (JsonException)
            { }
            #endregion

            #region Assets check
            valueSet = new ValueSet();
            valueSet["type"] = "assetsCheck";
            valueSet["version"] = option.versionId;
            response = await AppServiceManager.appServiceConnection.SendMessageAsync(valueSet);

            object obj = null;
            response.Message.TryGetValue("index_path", out obj);
            // Asset index dose not exist or invalid
            if (obj != null)
            {
                string path = obj.ToString();
                string url = response.Message["index_url"].ToString();

                try
                {
                    using (HttpClient client = new HttpClient())
                    {
                        string json = await client.GetStringAsync(url);
                        StorageFile file = await CoreManager.WorkDir.CreateFileAsync(path, CreationCollisionOption.ReplaceExisting);
                        await FileIO.WriteTextAsync(file, json);
                    }
                }
                catch (Exception e)
                {
                    await _msgDialog.Show(
                        CoreManager.GetStringFromResource("/StartPage/LaunchFailed"),
                        "Cannot fetch asset index \r\n " + e.Message + "\r\n" + e.StackTrace
                        );
                    return;
                }

                //Check again after asset index downloaded
                response = await AppServiceManager.appServiceConnection.SendMessageAsync(valueSet);
                obj = null;
                response.Message.TryGetValue("index_path", out obj);
                if (obj != null)
                {
                    await _msgDialog.Show(
                        CoreManager.GetStringFromResource("/StartPage/LaunchFailed"),
                        "Asset index validation failed");
                    return;
                }
            }

            responseJson = response.Message["missing_assets"].ToString();
            try
            {
                missingAssets = JsonConvert.DeserializeObject<List<MinecraftAsset>>(responseJson);
            }
            catch (JsonException) { }
            #endregion

            //Found missing libs, go to download.
            if ((missingLibs != null && missingLibs.Count > 0) || (missingAssets != null && missingAssets.Count > 0))
            {
                missingLibs.ForEach(lib =>
                {
                    DownloadItem item = new DownloadItem(lib.Name, lib.Path, lib.Url);
                    DownloadManager.DownloadQuene.Add(item);
                });
                missingAssets.ForEach(ass =>
                {
                    DownloadItem item = new DownloadItem(
                            string.Format("{0}: {1}", CoreManager.GetStringFromResource("/Resources/Asset"), ass.Hash),
                            ass.GetPath(),
                            ass.GetDownloadUrl()
                        );
                    DownloadManager.DownloadQuene.Add(item);
                });

                DownloadManager.StartDownload();
                await DownloadDialog.ShowAsync();

                return;
            }

            DebugWriteLine("Serializing launch message to json");

            string messageJson;
            try
            {
                LaunchOptionBase tmp = option as LaunchOptionBase;
                if (string.IsNullOrWhiteSpace(option.javaExt))
                {
                    tmp.javaExt = CoreManager.GlobalJVMPath;
                }
                messageJson = JsonConvert.SerializeObject(tmp);
            }
            catch (JsonSerializationException exp)
            {
                DebugWriteLine("ERROR: " + exp.Message);
                return;
            }

            DebugWriteLine(messageJson);

            //Check if the launch message was successfully generated
            if (!string.IsNullOrWhiteSpace(messageJson))
            {
                valueSet = new ValueSet();
                valueSet.Add("type", "launch");
                valueSet.Add("launch_option", messageJson);
                valueSet.Add("auth_type", CoreManager.AccountTypeTag);
                valueSet.Add("auth_username", CoreManager.Username);
                response = await AppServiceManager.appServiceConnection.SendMessageAsync(valueSet);

                //Display error
                obj = response.Message["result"];
                if (obj is bool && !((bool)obj))
                {
                    await _msgDialog.Show(
                        CoreManager.GetStringFromResource("/StartPage/LaunchFailed"), 
                        response.Message["errorMessage"].ToString() + "\r\n" + response.Message["errorStack"]
                        );
                }
            }

        }

        private static void DebugWriteLine(string str)
        {
#if DEBUG
            System.Diagnostics.Debug.WriteLine(str);
#endif
        }

        private void DownloadDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            DownloadManager.CancelAllDownload();
        }
    }
}
