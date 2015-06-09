﻿using System.Text.RegularExpressions;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace EasyImgur
{
    public partial class Form1 : Form
    {
        private bool CloseCommandWasSentFromExitButton = false;
        private enum MessageVerbosity { Normal = 0, NoInfo = 1, NoError = 2 }
        private MessageVerbosity Verbosity = MessageVerbosity.Normal;

        /// <summary>
        /// Property to easily access the path of the executable, quoted for safety.
        /// </summary>
        private string QuotedApplicationPath { get { return "\"" + Application.ExecutablePath + "\""; } }

        public Form1(SingleInstance _SingleInstance, string[] _Args)
        {
            InitializeComponent();
            Interop.SetCueBanner(textBoxClipboardFormat, "http://i.imgur.com/%id%.%ext%", false);

            ImplementPortableMode();

            CreateHandle(); // force the handle to be created so Invoke succeeds; see issue #8 for more detail

            this.notifyIcon1.ContextMenu = this.trayMenu;

            Application.ApplicationExit += new System.EventHandler(this.ApplicationExit);

            InitializeEventHandlers();
            History.BindData(historyItemBindingSource); // to use the designer with data binding, we have to pass History our BindingSource, instead of just getting one from History
            History.InitializeFromDisk();

            // if we have arguments, we're going to show a tip when we handle those arguments. 
            if(_Args.Length == 0) 
                ShowBalloonTip(2000, "EasyImgur is ready for use!", "Right-click EasyImgur's icon in the tray to use it!", ToolTipIcon.Info);

            ImgurAPI.AttemptRefreshTokensFromDisk();

            Statistics.GatherAndSend();

            _SingleInstance.ArgumentsReceived += singleInstance_ArgumentsReceived;
            if(_Args.Length > 0) // handle initial arguments
                singleInstance_ArgumentsReceived(this, new ArgumentsReceivedEventArgs() { Args = _Args });
        }

        private void InitializeEventHandlers()
        {
            this.FormClosing += new System.Windows.Forms.FormClosingEventHandler(this.Form1_Closing);
            this.Load += new System.EventHandler(this.Form1_Load);
            notifyIcon1.BalloonTipClicked += new System.EventHandler(this.NotifyIcon1_BalloonTipClicked);
            notifyIcon1.MouseDoubleClick += new System.Windows.Forms.MouseEventHandler(this.NotifyIcon1_MouseDoubleClick);
            tabControl1.SelectedIndexChanged += new System.EventHandler(this.tabControl1_SelectedIndexChanged);

            ImgurAPI.obtainedAuthorization += new ImgurAPI.AuthorizationEventHandler(this.ObtainedAPIAuthorization);
            ImgurAPI.refreshedAuthorization += new ImgurAPI.AuthorizationEventHandler(this.RefreshedAPIAuthorization);
            ImgurAPI.lostAuthorization += new ImgurAPI.AuthorizationEventHandler(this.LostAPIAuthorization);
            ImgurAPI.networkRequestFailed += new ImgurAPI.NetworkEventHandler(this.APINetworkRequestFailed);
        }

        void ImplementPortableMode()
        {
            if (Program.InPortableMode)
            {
                this.checkBoxLaunchAtBoot.Enabled = false;
                this.checkBoxEnableContextMenu.Enabled = false;
                this.Text += " - Portable Mode";
            }
            else
            {
                this.labelPortableModeNote.Visible = false;
            }
        }

        bool ShouldShowMessage(MessageVerbosity _Verbosity)
        {
            return Verbosity < _Verbosity;
        }

        void singleInstance_ArgumentsReceived( object sender, ArgumentsReceivedEventArgs e )
        {
            // Using "/exit" anywhere in the command list will cause EasyImgur to exit after uploading;
            // this will happen regardless of the execution sending the exit command was the execution that
            // launched the initial instance of EasyImgur.
            bool exitWhenFinished = false;
            bool anonymous = false;

            // mappings of switch names to actions
            Dictionary<string, Action> handlers = new Dictionary<string, Action>() {
                { "anonymous", () => anonymous = true },
                { "noinfo", () => Verbosity = (MessageVerbosity)Math.Max((int)Verbosity, (int)MessageVerbosity.NoInfo) },
                { "q", () => Verbosity = (MessageVerbosity)Math.Max((int)Verbosity, (int)MessageVerbosity.NoInfo) },
                { "noerr", () => Verbosity = (MessageVerbosity)Math.Max((int)Verbosity, (int)MessageVerbosity.NoError) },
                { "qq", () => Verbosity = (MessageVerbosity)Math.Max((int)Verbosity, (int)MessageVerbosity.NoError) },
                { "exit", () => exitWhenFinished = true },
                { "portable", () => { } } // ignore
            };
            
            try
            {
                // First scan for switches
                int badSwitchCount = 0;
                foreach (String str in e.Args.Where(s => s != null && s.StartsWith("/")))
                {
                    String param = str.Remove(0, 1); // Strip the leading '/' from the switch.

                    if (handlers.ContainsKey(param))
                    {
                        Log.Warning("Consuming command-line switch '" + param + "'.");
                        handlers[param]();
                    }
                    else
                    {
                        ++badSwitchCount;
                        Log.Warning("Ignoring unrecognized command-line switch '" + param + "'.");
                    }
                }

                if (badSwitchCount > 0 && ShouldShowMessage(MessageVerbosity.NoError))
                {
                    ShowBalloonTip(2000, "Invalid switch", badSwitchCount.ToString() + " invalid switch" + (badSwitchCount > 1 ? "es were" : " was") + " passed to EasyImgur (see log for details). No files were uploaded.", ToolTipIcon.Error, true);
                    return;
                }

                // Process actual arguments
                foreach(string path in e.Args.Where(s => s != null && !s.StartsWith("/")))
                {
                    if(!anonymous && !ImgurAPI.HasBeenAuthorized())
                    {
                        ShowBalloonTip(2000, "Not logged in", "You aren't logged in but you're trying to upload to an account. Authorize EasyImgur and try again.", ToolTipIcon.Error, true);
                        return;
                    }

                    if(Directory.Exists(path))
                    {
                        string[] fileTypes = new[] { ".jpg", ".jpeg", ".png", ".apng", ".bmp",
                            ".gif", ".tiff", ".tif", ".xcf" };
                        List<string> files = new List<string>();
                        foreach (string s in Directory.GetFiles(path))
                        {
                            bool cont = false;
                            foreach (string filetype in fileTypes)
                                if (s.EndsWith(filetype, true, null))
                                {
                                    cont = true;
                                    break;
                                }
                            if (!cont)
                                continue;

                            files.Add(s);
                        }

                        UploadAlbum(anonymous, files.ToArray(), path.Split('\\').Last());
                    }
                    else if(File.Exists(path))
                        UploadFile(anonymous, new string[] { path });
                }
            }
            catch(Exception ex)
            {
                Log.Error("Unhandled exception in context menu thread: " + ex.ToString());
                ShowBalloonTip(2000, "Error", "An unknown exception occurred during upload. Check the log for further information.", ToolTipIcon.Error, true);
            }

            if(exitWhenFinished)
                Application.Exit();

            // reset verbosity
            Verbosity = MessageVerbosity.Normal;
        }

        private void ShowBalloonTip( int _Timeout, string _Title, string _Text, ToolTipIcon _Icon, bool error = false )
        {
            if (ShouldShowMessage(error ? MessageVerbosity.NoError : MessageVerbosity.NoInfo))
            {
                if (notifyIcon1 != null)
                    notifyIcon1.ShowBalloonTip(_Timeout, _Title, _Text, _Icon);
                Log.Info(string.Format("Showed tooltip with title \"{0}\" and text \"{1}\".", _Title, _Text));
            }
            else
            {
                Log.Info(string.Format("Tooltip with title \"{0}\" and text \"{1}\" was suppressed.", _Title, _Text));
            }
        }

        private void ApplicationExit( object sender, EventArgs e )
        {
            ImgurAPI.OnMainThreadExit();
            if (notifyIcon1 != null)
                notifyIcon1.Visible = false;
        }

        private void ObtainedAPIAuthorization()
        {
            SetAuthorizationStatusUI(true);
            ShowBalloonTip(2000, "EasyImgur", "EasyImgur has received authorization to use your Imgur account!", ToolTipIcon.Info);
        }

        private void RefreshedAPIAuthorization()
        {
            SetAuthorizationStatusUI(true);
            if (Properties.Settings.Default.showNotificationOnTokenRefresh)
                ShowBalloonTip(2000, "EasyImgur", "EasyImgur has successfully refreshed authorization tokens!", ToolTipIcon.Info);
        }

        private void SetAuthorizationStatusUI( bool _IsAuthorized )
        {
            uploadClipboardAccountTrayMenuItem.Enabled = _IsAuthorized;
            uploadFileAccountTrayMenuItem.Enabled = _IsAuthorized;
            label13.Text = _IsAuthorized ? "Authorized" : "Not authorized";
            label13.ForeColor = _IsAuthorized ? System.Drawing.Color.Green : System.Drawing.Color.DarkBlue;
            buttonForceTokenRefresh.Enabled = _IsAuthorized;
            buttonForgetTokens.Enabled = _IsAuthorized;
        }

        private void LostAPIAuthorization()
        {
            SetAuthorizationStatusUI(false);
            ShowBalloonTip(2000, "EasyImgur", "EasyImgur no longer has authorization to use your Imgur account!", ToolTipIcon.Info);
        }

        private void APINetworkRequestFailed()
        {
            ShowBalloonTip(2000, "EasyImgur", "Network request failed. Check your internet connection.", ToolTipIcon.Error, true);
        }

        private void tabControl1_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (tabControl1.SelectedIndex == 2)
            {
                SelectedHistoryItemChanged();
            }
        }

        private void UploadClipboard( bool _Anonymous )
        {
            APIResponses.ImageResponse resp = null;
            Image clipboardImage = null;
            string clipboardURL = string.Empty;
            //bool anonymous = !Properties.Settings.Default.useAccount || !ImgurAPI.HasBeenAuthorized();
            bool anonymous = _Anonymous;
            if (Clipboard.ContainsImage())
            {
                clipboardImage = Clipboard.GetImage();
                ShowBalloonTip(4000, "Hold on...", "Attempting to upload image to Imgur...", ToolTipIcon.None);
                resp = ImgurAPI.UploadImage(clipboardImage, GetTitleString(null), GetDescriptionString(null), _Anonymous);
            }
            else if (Clipboard.ContainsText())
            {
                clipboardURL = Clipboard.GetText(TextDataFormat.UnicodeText);
                Uri uriResult;
                if (Uri.TryCreate(clipboardURL, UriKind.Absolute, out uriResult) && (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps))
                {
                    ShowBalloonTip(4000, "Hold on...", "Attempting to upload image to Imgur...", ToolTipIcon.None);
                    resp = ImgurAPI.UploadImage(clipboardURL, GetTitleString(null), GetDescriptionString(null), _Anonymous);
                }
                else
                {
                    ShowBalloonTip(2000, "Can't upload clipboard!", "There's text on the clipboard but it's not a valid URL", ToolTipIcon.Error, true);
                    return;
                }
            }
            else
            {
                ShowBalloonTip(2000, "Can't upload clipboard!", "There's no image or URL there", ToolTipIcon.Error, true);
                return;
            }
            if (resp.success)
            {
                // this doesn't need an invocation guard because this function can't be called from the context menu
                if (Properties.Settings.Default.copyLinks)
                {
                    string text = Properties.Settings.Default.useCustomClipboardFormat
                        ? FormatClipboardLink(Properties.Settings.Default.customClipboardFormat, resp.data.id, GetExtensionFromResponse(resp.data))
                        : resp.data.link;
                    Clipboard.SetText(text);
                }

                ShowBalloonTip(2000, "Success!", Properties.Settings.Default.copyLinks ? "Link copied to clipboard" : "Upload placed in history: " + resp.data.link, ToolTipIcon.None);

                HistoryItem item = new HistoryItem();
                item.timestamp = DateTime.Now;
                item.id = resp.data.id;
                item.link = resp.data.link;
                item.deletehash = resp.data.deletehash;
                item.title = resp.data.title;
                item.description = resp.data.description;
                item.anonymous = anonymous;
                if (clipboardImage != null)
                    item.thumbnail = clipboardImage.GetThumbnailImage(pictureBox1.Width, pictureBox1.Height, null, System.IntPtr.Zero);
                History.StoreHistoryItem(item);
            }
            else
                ShowBalloonTip(2000, "Failed", "Could not upload image (" + resp.status + "):", ToolTipIcon.None, true);

            if (!Properties.Settings.Default.clearClipboardOnUpload)
            {
                if (clipboardImage != null)
                    Clipboard.SetImage(clipboardImage);
                else
                    Clipboard.SetText(clipboardURL, TextDataFormat.UnicodeText);
            }
        }

        private void UploadAlbum( bool _Anonymous, string[] _Paths, string _AlbumTitle )
        {
            ShowBalloonTip(2000, "Hold on...", "Attempting to upload album to Imgur (this may take a while)...", ToolTipIcon.None);
            List<Image> images = new List<Image>();
            List<string> titles = new List<string>();
            List<string> descriptions = new List<string>();
            int i = 0;
            foreach (string path in _Paths)
            {
                try
                {
                    images.Add(Image.FromStream(new MemoryStream(File.ReadAllBytes(path))));
                    //ìmages.Add(System.Drawing.Image.FromStream(stream));
                    string title = string.Empty;
                    string description = string.Empty;

                    FormattingHelper.FormattingContext format_context = new FormattingHelper.FormattingContext();
                    format_context.m_Filepath = path;
                    format_context.m_AlbumIndex = ++i;
                    titles.Add(GetTitleString(format_context));
                    descriptions.Add(GetDescriptionString(format_context));
                }
                catch(FileNotFoundException)
                {
                    ShowBalloonTip(2000, "Failed", "Could not find image file on disk (" + path + "):", ToolTipIcon.Error, true);
                }
                catch(IOException)
                {
                    ShowBalloonTip(2000, "Failed", "Image is in use by another program (" + path + "):", ToolTipIcon.Error, true);
                }
            }

            APIResponses.AlbumResponse response = ImgurAPI.UploadAlbum(images.ToArray(), _AlbumTitle, _Anonymous, titles.ToArray(), descriptions.ToArray());
            if(response.success)
            {
                string text = Properties.Settings.Default.useCustomClipboardFormat
                        ? FormatClipboardLink(Properties.Settings.Default.customClipboardFormat, response.data.id)
                        : response.data.link;
                // clipboard calls can only be made on an STA thread, threading model is MTA when invoked from context menu
                if(System.Threading.Thread.CurrentThread.GetApartmentState() != System.Threading.ApartmentState.STA)
                    this.Invoke(new Action(() => Clipboard.SetText(text)));
                else
                    Clipboard.SetText(text);

                ShowBalloonTip(2000, "Success!", Properties.Settings.Default.copyLinks ? "Link copied to clipboard" : "Upload placed in history: " + response.data.link, ToolTipIcon.None);

                HistoryItem item = new HistoryItem();
                item.timestamp = DateTime.Now;
                item.id = response.data.id;
                item.link = response.data.link;
                item.deletehash = response.data.deletehash;
                item.title = response.data.title;
                item.description = response.data.description;
                item.anonymous = _Anonymous;
                item.album = true;
                item.thumbnail = response.CoverImage.GetThumbnailImage(pictureBox1.Width, pictureBox1.Height, null, System.IntPtr.Zero);
                Invoke(new Action(() => { History.StoreHistoryItem(item); }));
            }
            else
                ShowBalloonTip(2000, "Failed", "Could not upload album (" + response.status + "): " + response.data.error, ToolTipIcon.None, true);
        }

        private void UploadFile( bool _Anonymous, string[] _Paths = null )
        {
            if(_Paths == null)
            {
                OpenFileDialog dialog = new OpenFileDialog();
                dialog.Multiselect = true;
                DialogResult res = dialog.ShowDialog();
                if(res == DialogResult.OK)
                    _Paths = dialog.FileNames;
            }
            if (_Paths != null)
            {
                if(_Paths.Length > 1 && Properties.Settings.Default.uploadMultipleImagesAsGallery)
                {
                    UploadAlbum(_Anonymous, _Paths, "");
                    return;
                }

                int success = 0;
                int failure = 0;
                int i = 0;
                foreach (string fileName in _Paths)
                {
                    ++i;

                    if (fileName == null || fileName == string.Empty)
                        continue;

                    string fileCounterString = (_Paths.Length > 1) ? (" (" + i.ToString() + "/" + _Paths.Length.ToString() + ") ") : (string.Empty);

                    try
                    {
                        ShowBalloonTip(2000, "Hold on..." + fileCounterString, "Attempting to upload image to Imgur...", ToolTipIcon.None);
                        Image img;
                        APIResponses.ImageResponse resp;
                        using(System.IO.FileStream stream = System.IO.File.Open(fileName, System.IO.FileMode.Open))
                        {
                            // a note to the future: ImgurAPI.UploadImage called Image.Save(); Image.Save()
                            // requires that the stream it was loaded from still be open, or else you get
                            // an immensely generic error. 
                            img = System.Drawing.Image.FromStream(stream);
                            FormattingHelper.FormattingContext format_context = new FormattingHelper.FormattingContext();
                            format_context.m_Filepath = fileName;
                            resp = ImgurAPI.UploadImage(img, GetTitleString(format_context), GetDescriptionString(format_context), _Anonymous);
                        }
                        if (resp.success)
                        {
                            success++;

                            if (Properties.Settings.Default.copyLinks)
                            {
                                string text = Properties.Settings.Default.useCustomClipboardFormat
                                    ? FormatClipboardLink(Properties.Settings.Default.customClipboardFormat, resp.data.id, GetExtensionFromResponse(resp.data))
                                    : resp.data.link;
                                // clipboard calls can only be made on an STA thread, threading model is MTA when invoked from context menu
                                if(System.Threading.Thread.CurrentThread.GetApartmentState() != System.Threading.ApartmentState.STA)
                                    this.Invoke(new Action(() => Clipboard.SetText(text)));
                                else
                                    Clipboard.SetText(text);
                            }

                            ShowBalloonTip(2000, "Success!" + fileCounterString, Properties.Settings.Default.copyLinks ? "Link copied to clipboard" : "Upload placed in history: " + resp.data.link, ToolTipIcon.None);

                            HistoryItem item = new HistoryItem(); 
                            item.timestamp = DateTime.Now;
                            item.id = resp.data.id;
                            item.link = resp.data.link;
                            item.deletehash = resp.data.deletehash;
                            item.title = resp.data.title;
                            item.description = resp.data.description;
                            item.anonymous = _Anonymous;
                            item.thumbnail = img.GetThumbnailImage(pictureBox1.Width, pictureBox1.Height, null, System.IntPtr.Zero);
                            Invoke(new Action(() => { History.StoreHistoryItem(item); }));
                        }
                        else
                        {
                            failure++;
                            ShowBalloonTip(2000, "Failed" + fileCounterString, "Could not upload image (" + resp.status + "): " + resp.data.error, ToolTipIcon.None, true);
                        }
                    }
                    catch (System.IO.FileNotFoundException)
                    {
                        failure++;
                        ShowBalloonTip(2000, "Failed" + fileCounterString, "Could not find image file on disk (" + fileName + "):", ToolTipIcon.Error, true);
                    }
                    catch(IOException)
                    {
                        ShowBalloonTip(2000, "Failed" + fileCounterString, "Image is in use by another program (" + fileName + "):", ToolTipIcon.Error, true);
                    }
                }
                if(_Paths.Length > 1)
                {
                    ShowBalloonTip(2000, "Done", "Successfully uploaded " + success.ToString() + " files" + ((failure > 0) ? (" (Warning: " + failure.ToString() + " failed)") : (string.Empty)), ToolTipIcon.Info, failure > 0);
                }
            }
        }

        private void buttonOK_Click(object sender, EventArgs e)
        {
            // Should we remind the user here somehow?
            //if (checkBoxClipboardFormat.Enabled && String.IsNullOrWhiteSpace(textBoxClipboardFormat.Text))
            //{
            //    // Custom clipboard format is enabled but no format is specified
            //}
            SaveSettings();
        }

        private void NotifyIcon1_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            this.Show();
            this.Activate();
            this.Focus();
            this.BringToFront();
        }

        private void NotifyIcon1_BalloonTipClicked(object sender, EventArgs e)
        {
            /*if (!Properties.Settings.Default.copyLinks)
            {

            }*/
            this.Show();
            tabControl1.SelectedIndex = 2;
            listBoxHistory.SelectedIndex = listBoxHistory.Items.Count - 1;
            this.BringToFront();
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            Properties.Settings.Default.Reload();

            // Assign control values. Most values are set using application binding on the control.
            comboBoxImageFormat.SelectedIndex = Properties.Settings.Default.imageFormat;

            // Check the registry for a key describing whether EasyImgur should be started on boot.
            Microsoft.Win32.RegistryKey registryKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);
            string value = (string)registryKey.GetValue("EasyImgur", string.Empty); // string.Empty is returned if no key is present.
            checkBoxLaunchAtBoot.Checked = value != string.Empty;
            if(value != string.Empty && value != QuotedApplicationPath)
            {
                // A key exists, make sure we're using the most up-to-date path!
                registryKey.SetValue("EasyImgur", QuotedApplicationPath);
                ShowBalloonTip(2000, "EasyImgur", "Updated registry path", ToolTipIcon.Info);
            }
            UpdateRegistry(true); // this will need to be updated too, if we're using it

            // Bind the data source for the list of contributors.
            Contributors.bindingSource.DataSource = Contributors.contributors;
            contributorsList.DataSource = Contributors.bindingSource;
        }

        private void Form1_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            SaveSettings();
            this.Hide();

            e.Cancel = !CloseCommandWasSentFromExitButton;  // Don't want to *actually* close the form unless the Exit button was used.
        }

        private void SaveSettings()
        {
            // Store control values. Most values are stored using application binding on the control.
            Properties.Settings.Default.imageFormat = comboBoxImageFormat.SelectedIndex;

            using(Microsoft.Win32.RegistryKey registryKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true))
            {
                if(checkBoxLaunchAtBoot.Checked)
                {
                    // If the checkbox was marked, set a value which will make EasyImgur start at boot.
                    registryKey.SetValue("EasyImgur", QuotedApplicationPath);
                }
                else
                {
                    // Delete our value if one is present; second argument supresses exception on missing value
                    registryKey.DeleteValue("EasyImgur", false);
                }
            }

            UpdateRegistry(false);

            Properties.Settings.Default.Save();
        }

        private void UpdateRegistry(bool _LoadingSettings)
        {
            if(_LoadingSettings) // update enableContextMenu if necessary
            {
                // look for one of our handlers; Directory is the easiest and most reliable
                // Since HKCR is a merging of HKCU\Software\Classes and HKLM\Software\Classes, look there
                using(RegistryKey dir = Registry.ClassesRoot.OpenSubKey("Directory"))
                using(RegistryKey shell = dir.OpenSubKey("shell"))
                    Properties.Settings.Default.enableContextMenu = shell != null && shell.OpenSubKey("imguruploadanonymous") != null;
            }

            // a note: Directory doesn't work if within SystemFileAssociations, and 
            // the extensions don't work if not inside them. At least, this seems to be the case for me

            // another note: I discovered that I had the logic flipped, and the code actually did the opposite
            // of what I describe in the above note, and it was working. Earlier, though, when I wrote that,
            // it seemed to be true. Either way, the placement (inside or outside of SystemFileAssociations)
            // does affect where in the context menu they show up. Feel free to play with the placement and see
            // if you can get it to work better.
            string[] fileTypes = new[] { ".jpg", ".jpeg", ".png", ".apng", ".bmp",
            ".gif", ".tiff", ".tif", ".pdf", ".xcf", "Directory" };
            using(RegistryKey root = Registry.CurrentUser.OpenSubKey("Software\\Classes", true))
            using(RegistryKey fileAssoc = root.CreateSubKey("SystemFileAssociations"))
                foreach(string fileType in fileTypes)
                    using(RegistryKey fileTypeKey = (fileType != "Directory" ? fileAssoc.CreateSubKey(fileType) : root.CreateSubKey(fileType)))
                    using(RegistryKey shell = fileTypeKey.CreateSubKey("shell"))
                    {
                        if(Properties.Settings.Default.enableContextMenu)
                        {
                            using(RegistryKey anonHandler = shell.CreateSubKey("imguruploadanonymous"))
                                EnableContextMenu(anonHandler, "Upload to Imgur" +
                                    (fileType == "Directory" ? " as album" : "") + " (anonymous)", true);
                            using(RegistryKey accHandler = shell.CreateSubKey("imgurupload"))
                                EnableContextMenu(accHandler, "Upload to Imgur" +
                                    (fileType == "Directory" ? " as album" : ""), false);
                        }
                        else
                        {
                            try { shell.DeleteSubKeyTree("imguruploadanonymous"); } catch { }
                            try { shell.DeleteSubKeyTree("imgurupload"); } catch { }
                        }
                    }
        }

        private void EnableContextMenu(RegistryKey key, string commandText, bool anonymous)
        {
            key.SetValue("", commandText);
            key.SetValue("Icon", QuotedApplicationPath);
            using(RegistryKey subKey = key.CreateSubKey("command"))
                subKey.SetValue("", QuotedApplicationPath + (anonymous ? " /anonymous" : "") + " \"%1\"");
        }

        private void buttonChangeCredentials_Click(object sender, EventArgs e)
        {
            AuthorizeForm accountCredentialsForm = new AuthorizeForm();
            DialogResult res = accountCredentialsForm.ShowDialog(this);
            
            if (ImgurAPI.HasBeenAuthorized())
            {
                buttonForceTokenRefresh.Enabled = true;
            }
            else
            {
                buttonForceTokenRefresh.Enabled = false;
            }
        }

        private void SelectedHistoryItemChanged()
        {
            HistoryItem item = listBoxHistory.SelectedItem as HistoryItem;
            if (item == null)
            {
                buttonRemoveFromImgur.Enabled = false;
                buttonRemoveFromHistory.Enabled = false;
                btnOpenImageLinkInBrowser.Enabled = false;
            }
            else
            {
                buttonRemoveFromImgur.Enabled = item.anonymous || (!item.anonymous && ImgurAPI.HasBeenAuthorized());
                buttonRemoveFromHistory.Enabled = true;
                btnOpenImageLinkInBrowser.Enabled = true;
            }
        }

        private void listBoxHistory_SelectedIndexChanged(object sender, EventArgs e)
        {
            SelectedHistoryItemChanged();
        }

        private string FormatInfoString( string _Input, FormattingHelper.FormattingContext _FormattingContext )
        {
            return FormattingHelper.Format(_Input, _FormattingContext);
        }

        // Move this to an utility class?
        /// <summary>
        /// Performs custom clipboard format replacements on a string.
        /// </summary>
        /// <param name="format">Custom clipboard link format</param>
        /// <param name="id">Imgur image or album ID</param>
        /// <param name="extension">Imgur image file extension. Do not specify if formatting album link. Defaults to "".</param>
        /// <returns>Formatted string</returns>
        static public string FormatClipboardLink(string format, string id, string extension = "")
        {
            return format.Replace("%id%", id).Replace("%ext%", extension);
        }

        private string GetTitleString( FormattingHelper.FormattingContext _FormattingContext )
        {
            return FormatInfoString(textBoxTitleFormat.Text, _FormattingContext);
        }

        private string GetDescriptionString(FormattingHelper.FormattingContext _FormattingContext)
        {
            return FormatInfoString(textBoxDescriptionFormat.Text, _FormattingContext);
        }

        // Move this to an utility class?
        /// <summary>
        /// Gets the uploaded image's file extension.
        /// </summary>
        /// <param name="data">Response data from which the link's extension will be parsed.</param>
        /// <returns>String containing the file's extension without a period.</returns>
        private string GetExtensionFromResponse(APIResponses.ImageResponse.Data data)
        {
            //  \.              Match a period
            //  (               Begin capture group
            //      [a-zA-Z]    Match any character from these ranges
            //      {3,4}       Match 3 to 4 characters of previous criteria
            //  )               End capture group
            //  $               End of line anchor
            var regex = new Regex(@"\.([a-zA-Z]{3,4})$");
            Match match = regex.Match(data.link);
            return match.Success
                ? match.Groups[1].Value
                : "";
        }

        private void buttonRemoveFromImgur_Click(object sender, EventArgs e)
        {
            int count = listBoxHistory.SelectedItems.Count;
            bool isMultipleImages = count > 1;
            int currentCount = 0;

            listBoxHistory.BeginUpdate();
            List<HistoryItem> selectedItems = new List<HistoryItem>(listBoxHistory.SelectedItems.Cast<HistoryItem>());
            foreach(HistoryItem item in selectedItems)
            {
                ++currentCount;

                if (item == null)
                    return;

                string balloon_image_counter_text = (isMultipleImages ? (currentCount.ToString() + "/" + count.ToString()) : string.Empty);
                string balloon_text = "Attempting to remove " + (item.album ? "album" : "image") + " " + balloon_image_counter_text + " from Imgur...";
                ShowBalloonTip(2000, "Hold on...", balloon_text, ToolTipIcon.None);
                if (item.album ? ImgurAPI.DeleteAlbum(item.deletehash, item.anonymous) : ImgurAPI.DeleteImage(item.deletehash, item.anonymous))
                {
                    ShowBalloonTip(2000, "Success!", "Removed " + (item.album ? "album" : "image") + " " + balloon_image_counter_text + " from Imgur and history", ToolTipIcon.None);
                    History.RemoveHistoryItem(item);
                }
                else
                    ShowBalloonTip(2000, "Failed", "Failed to remove " + (item.album ? "album" : "image") + " " + balloon_image_counter_text + " from Imgur", ToolTipIcon.Error);
            }
            listBoxHistory.EndUpdate();
        }

        private void buttonForceTokenRefresh_Click(object sender, EventArgs e)
        {
            ImgurAPI.ForceRefreshTokens();
            SetAuthorizationStatusUI(ImgurAPI.HasBeenAuthorized());
        }

        private void buttonFormatHelp_Click(object sender, EventArgs e)
        {
            FormattingHelper.FormattingScheme[] formattingSchemes = FormattingHelper.GetSchemes();
            string helpString = "You can use strings consisting of either static characters or the following dynamic symbols, or a combination of both:\n\n";
            foreach (FormattingHelper.FormattingScheme scheme in formattingSchemes)
            {
                helpString += scheme.symbol + "  :  " + scheme.description + "\n";
            }
            string exampleFormattedString = "Image_%date%_%time%";
            helpString += "\n\nEx.: '" + exampleFormattedString + "' would become: '" + FormattingHelper.Format(exampleFormattedString, null);
            Point loc = this.Location;
            loc.Offset(buttonFormatHelp.Location.X, buttonFormatHelp.Location.Y);
            Help.ShowPopup(this, helpString, loc);
        }

        private void buttonForgetTokens_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show("Are you sure you want to discard the tokens?\n\nWithout tokens, the app can no longer use your Imgur account.", "Forget tokens", MessageBoxButtons.YesNo) == DialogResult.Yes)
            {
                ImgurAPI.ForgetTokens();
                MessageBox.Show("Tokens have been forgotten. Remember that the app has still technically been authorized on the Imgur website, we can't change this for you!\n\nGo to http://imgur.com/account/settings/apps to revoke access.", "Tokens discarded", MessageBoxButtons.OK);
            }
        }

        private void buttonRemoveFromHistory_Click(object sender, EventArgs e)
        {
            listBoxHistory.BeginUpdate();
            List<HistoryItem> selectedItems = new List<HistoryItem>(listBoxHistory.SelectedItems.Cast<HistoryItem>());
            foreach(HistoryItem item in selectedItems)
            {
                if (item == null)
                {
                    return;
                }

                History.RemoveHistoryItem(item);
            }
            listBoxHistory.EndUpdate();
        }

        private void uploadClipboardAccountTrayMenuItem_Click(object sender, EventArgs e)
        {
            UploadClipboard(false);
        }

        private void uploadFileAccountTrayMenuItem_Click(object sender, EventArgs e)
        {
            UploadFile(false);
        }

        private void uploadClipboardAnonymousTrayMenuItem_Click(object sender, EventArgs e)
        {
            UploadClipboard(true);
        }

        private void uploadFileAnonymousTrayMenuItem_Click(object sender, EventArgs e)
        {
            UploadFile(true);
        }

        private void settingsTrayMenuItem_Click(object sender, EventArgs e)
        {
            // Open settings form.
            this.Show();
        }

        private void exitTrayMenuItem_Click(object sender, EventArgs e)
        {
            CloseCommandWasSentFromExitButton = true;
            Application.Exit();
        }

        private void linkLabel2_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            System.Diagnostics.Process.Start("http://easyimgur.bryankeiren.com/");
        }

        private void linkLabel1_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            System.Diagnostics.Process.Start("http://bryankeiren.com/");
        }

        private void linkLabel3_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            System.Diagnostics.Process.Start("https://imgur.com/account/settings/apps");
        }

        private void btnOpenImageLinkInBrowser_Click(object sender, EventArgs e)
        {
            HistoryItem item = listBoxHistory.SelectedItem as HistoryItem;
            if (item != null)
            {
                System.Diagnostics.Process.Start(item.link);
            }
        }

        private void buttonClipboardFormatHelp_Click(object sender, EventArgs e)
        {
            const string helpString =
                "Use this to modify the text copied to clipboard after uploading an image. " +
                "You can use the following modifiers to insert information:\n" +
                "%id% - Imgur image ID of your uploaded image\n" +
                "%ext% - [Images only] Your uploaded image's image extension.\n\n" +
                "By default, the clipboard format is:\n" +
                "http://i.imgur.com/%id%.%ext%\n\n" +
                "Example:\n" +
                "http://imgur.com/%id%\n" +
                "https://i.imgur.com/%id%.%ext%";
            Point loc = this.Location;
            loc.Offset(buttonFormatHelp.Location.X, buttonFormatHelp.Location.Y);
            Help.ShowPopup(this, helpString, loc);
        }
    }
}
