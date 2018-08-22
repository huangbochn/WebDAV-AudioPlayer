﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ByteSizeLib;
using CSCore.SoundOut;
using WebDav.AudioPlayer.Audio;
using WebDav.AudioPlayer.Client;
using WebDav.AudioPlayer.Models;
using WebDav.AudioPlayer.Properties;

namespace WebDav.AudioPlayer.UI
{
    public partial class MainForm : Form
    {
        private readonly AssemblyConfig _config;
        private readonly IWebDavClient _client;
        private readonly Player _player;

        private CancellationTokenSource _cancelationTokenSource;
        private CancellationToken _cancelToken;

        public MainForm(AssemblyConfig config)
        {
            _config = config;

            InitializeComponent();

            InitDpi();

            Icon = Resources.icon;

            InitCancellationTokenSource();

            //_client = new DecaTecWebDavClient(config);
            _client = new MyWebDavClient(config);

            Func<ResourceItem, string, string> updateTitle = (resourceItem, action) =>
            {
                string bitrate = resourceItem.MediaDetails.Bitrate != null ? $"{resourceItem.MediaDetails.Bitrate / 1000}" : "?";
                string text = $"{action} : '{resourceItem.Parent.DisplayName}\\{resourceItem.DisplayName}' ({resourceItem.MediaDetails.Mode} {bitrate} kbps)";
                Text = @"WebDAV-AudioPlayer " + text;

                return text;
            };

            _player = new Player(_client)
            {
                Log = Log,

                PlayStarted = (selectedIndex, resourceItem) =>
                {
                    string bitrate = resourceItem.MediaDetails.Bitrate != null ? $"{resourceItem.MediaDetails.Bitrate / 1000}" : "?";
                    string text = updateTitle(resourceItem, "Playing");
                    textBoxSong.Text = text;

                    labelTotalTime.Text = $@"{_player.TotalTime:hh\:mm\:ss}";

                    trackBarSong.Maximum = (int)_player.TotalTime.TotalSeconds;

                    listView.SetSelectedIndex(selectedIndex);
                    listView.SetCells(selectedIndex, $@"{_player.TotalTime:h\:mm\:ss}", bitrate);
                },
                PlayContinue = resourceItem =>
                {
                    string text = updateTitle(resourceItem, "Playing");
                    textBoxSong.Text = text;
                },
                PlayPaused = resourceItem =>
                {
                    string text = updateTitle(resourceItem, "Pausing");
                    textBoxSong.Text = text;
                },
                PlayStopped = () =>
                {
                    trackBarSong.Value = 0;
                    trackBarSong.Maximum = 1;
                    labelCurrentTime.Text = labelTotalTime.Text = @"00:00:00";
                    Text = @"WebDAV-AudioPlayer";
                }
            };

            Log($"Using : '{_player.SoundOut}-SoundOut'");
        }

        /// <summary>
        /// https://stackoverflow.com/questions/22735174/how-to-write-winforms-code-that-auto-scales-to-system-font-and-dpi-settings/29766847#29766847
        /// </summary>
        private void InitDpi()
        {
            //var size = new SizeF(CreateGraphics().DpiX, CreateGraphics().DpiY).ToSize();
            //toolStripRight.AutoSize = false;
            //toolStripRight.ImageScalingSize = size;

            //toolStripTreeView.AutoSize = false;
            //toolStripRight.ImageScalingSize = size;

            // treeView.ImageList.ImageSize = size;
        }

        private void InitCancellationTokenSource()
        {
            _cancelationTokenSource?.Cancel();

            _cancelationTokenSource = new CancellationTokenSource();
            _cancelToken = _cancelationTokenSource.Token;
        }

        private void Log(string text)
        {
            txtLogging.AppendText($"{DateTime.Now} - {text}\r\n");
        }

        private async void MainForm_Load(object sender, EventArgs e)
        {
            await RefreshTreeAsync().ConfigureAwait(false);
        }

        private async Task RefreshTreeAsync()
        {
            var current = Cursor.Current;
            Cursor.Current = Cursors.WaitCursor;

            treeView.Nodes.Clear();

            var root = new ResourceItem
            {
                DisplayName = _config.RootFolder,
                FullPath = OnlinePathBuilder.ConvertPathToFullUri(_config.StorageUri, _config.RootFolder)
            };

            var result = await _client.FetchChildResourcesAsync(root, _cancelToken, 0);
            if (result != ResourceLoadStatus.Ok)
            {
                return;
            }

            var rootNode = new TreeNode
            {
                Text = _config.RootFolder,
                Tag = null
            };

            PopulateTree(ref rootNode, root.Items);

            treeView.Nodes.Add(rootNode);
            rootNode.Expand();
            Cursor.Current = current;
        }

        private void PopulateTree(ref TreeNode node, IList<ResourceItem> resourceItems)
        {
            if (resourceItems == null)
            {
                return;
            }

            if (node == null)
            {
                node = new TreeNode
                {
                    Text = _config.RootFolder,
                    Tag = null
                };
            }

            foreach (var resourceItem in resourceItems.Where(r => r.IsCollection))
            {
                var childNode = new TreeNode
                {
                    Text = resourceItem.DisplayName,
                    Tag = resourceItem,
                    ImageKey = @"Folder",
                    SelectedImageKey = @"Folder",
                    ContextMenuStrip = contextMenuStripOnFolder
                };

                PopulateTree(ref childNode, resourceItem.Items);
                node.Nodes.Add(childNode);
            }
        }

        private async void contextMenuStripOnFolder_ItemClicked(object sender, ToolStripItemClickedEventArgs e)
        {
            var resourceItem = e.ClickedItem.Tag as ResourceItem;
            if (resourceItem != null && e.ClickedItem.Name == "save")
            {
                await ShowFolderSaveDialogAsync(resourceItem);
            }
        }

        private async Task ShowFolderSaveDialogAsync(ResourceItem resourceItem)
        {
            if (folderBrowserDialog1.ShowDialog() == DialogResult.OK)
            {
                var progress = CreateAndShowDownloadFolderForm();

                Action<bool, ResourceItem, int, int> notify = (success, item, index, total) =>
                {
                    int pct = (int)(100.0 * index / total);
                    progress.lblPct.Text = $@"{pct}%";
                    progress.progressBar1.Value = pct;
                };

                ResourceLoadStatus status = ResourceLoadStatus.Unknown;
                try
                {
                    status = await _client.DownloadFolderAsync(resourceItem, folderBrowserDialog1.SelectedPath, notify, _cancelToken);

                    progress.lblPct.Text = "100%";
                    progress.progressBar1.Value = 100;

                    await Task.Delay(TimeSpan.FromMilliseconds(500), _cancelToken);
                }
                catch // (Exception e)
                {
                    // MessageBox.Show(e.Message, "Error saving folder.", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                }
                finally
                {
                    Log(status == ResourceLoadStatus.Ok
                        ? $"Folder '{resourceItem.DisplayName}' saved to '{folderBrowserDialog1.SelectedPath}'"
                        : $"Folder '{resourceItem.DisplayName}' was not saved correctly : {status}");

                    progress.Close();
                    InitCancellationTokenSource();
                }
            }
        }

        private DownloadFolderForm CreateAndShowDownloadFolderForm()
        {
            var progress = new DownloadFolderForm
            {
                Owner = this,
                CancellationTokenSource = _cancelationTokenSource,
                StartPosition = FormStartPosition.Manual
            };
            progress.Location = new Point(Location.X + (Width - progress.Width) / 2, Location.Y + (Height - progress.Height) / 2);
            progress.Show();
            return progress;
        }

        private async void treeView_NodeMouseClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            var hitTest = e.Node.TreeView.HitTest(e.Location);
            if (hitTest.Location == TreeViewHitTestLocations.PlusMinus)
                return;

            var resourceItem = e.Node.Tag as ResourceItem;
            if (resourceItem == null)
                return;

            if (e.Button == MouseButtons.Right)
            {
                contextMenuStripOnFolder.Items["save"].Tag = resourceItem;
                contextMenuStripOnFolder.Show(Cursor.Position);
                return;
            }

            var current = Cursor.Current;
            Cursor.Current = Cursors.WaitCursor;

            var result = await _client.FetchChildResourcesAsync(resourceItem, _cancelToken, resourceItem.Level, resourceItem.Level);
            if (result == ResourceLoadStatus.Ok)
            {
                var node = e.Node;
                node.Nodes.Clear();
                PopulateTree(ref node, resourceItem.Items);
                node.Expand();

                _player.Items = resourceItem.Items.Where(r => !r.IsCollection).ToList();

                listView.Items.Clear();
                foreach (var file in _player.Items)
                {
                    string size = file.ContentLength != null ? ByteSize.FromBytes(file.ContentLength.Value).ToString("0.00 MB") : string.Empty;
                    var listViewItem = new ListViewItem(new[] { file.DisplayName, size, null, null }) { Tag = file };
                    listView.Items.Add(listViewItem);
                }
            }

            Cursor.Current = current;
        }

        private void listView_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            _player.Play(listView.SelectedIndices[0], _cancelToken);
        }

        private void listView_KeyDown(object sender, KeyEventArgs e)
        {
            if (listView.Items.Count == 0)
                return;

            if (e.KeyData == Keys.Enter)
                _player.Play(listView.SelectedIndices[0], _cancelToken);

            if (e.KeyData == Keys.PageUp)
                listView.SetSelectedIndex(0);

            if (e.KeyData == Keys.PageDown)
                listView.SetSelectedIndex(listView.Items.Count - 1);

            if (e.KeyData == Keys.Up)
            {
                int upIndex = listView.SelectedIndices[0] - 1;
                if (upIndex > 0)
                    listView.SetSelectedIndex(upIndex);
            }

            if (e.KeyData == Keys.Down)
            {
                int downIndex = listView.SelectedIndices[0] + 1;
                if (downIndex < listView.Items.Count)
                    listView.SetSelectedIndex(downIndex);
            }
        }

        private void buttonPlay_Click(object sender, EventArgs e)
        {
            _player.Play(listView.SelectedIndices[0], _cancelToken);
        }

        private void buttonPause_Click(object sender, EventArgs e)
        {
            _player.Pause();
        }

        private void buttonStop_Click(object sender, EventArgs e)
        {
            _cancelationTokenSource.Cancel();
            _player.Stop(true);
            InitCancellationTokenSource();
        }

        private void btnPrevious_Click(object sender, EventArgs e)
        {
            _player.Previous(_cancelToken);
        }

        private void btnNext_Click(object sender, EventArgs e)
        {
            _player.Next(_cancelToken);
        }

        private void trackBarSong_Scroll(object sender, EventArgs e)
        {
            _player.JumpTo(TimeSpan.FromSeconds(trackBarSong.Value));
        }

        private async void btnRefresh_Click(object sender, EventArgs e)
        {
            await RefreshTreeAsync().ConfigureAwait(false);
        }

        private void trackBarSong_MouseDown(object sender, MouseEventArgs e)
        {
            _player.SetVolume(0);
        }

        private void trackBarSong_MouseUp(object sender, MouseEventArgs e)
        {
            _player.SetVolume(1);
        }

        private void audioPlaybackTimer_Tick(object sender, EventArgs e)
        {
            if (_player != null)
            {
                labelCurrentTime.Text = $@"{_player.CurrentTime:hh\:mm\:ss}";

                if (_player.PlaybackState == PlaybackState.Playing)
                {
                    trackBarSong.Value = (int)_player.CurrentTime.TotalSeconds;

                    if (_player.CurrentTime.Add(TimeSpan.FromMilliseconds(500)) > _player.TotalTime)
                    {
                        _player.Next(_cancelToken);
                    }
                }
            }
        }

        protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
        {
            base.ScaleControl(factor, specified);

            ScaleListViewColumns(listView, factor);
        }

        private void ScaleListViewColumns(ListView listview, SizeF factor)
        {
            foreach (ColumnHeader column in listview.Columns)
            {
                column.Width = (int)Math.Round(column.Width * factor.Width);
            }
        }

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && components != null)
            {
                components.Dispose();
            }

            _player.Dispose();

            _client.Dispose();

            base.Dispose(disposing);
        }
    }
}