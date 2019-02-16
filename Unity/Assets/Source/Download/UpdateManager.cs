﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using UnityEngine;

namespace Game
{
    /// <summary>
    /// 游戏热更新管理器
    /// 注:必须在资源管理器初始化完毕,确保能加载AB资源
    /// </summary>
    public partial class UpdateManager : IManager
    {
        public int LocalResVersion { get { return _localResVersion; } }
        public int LocalAppVersion { get { return _localAppVersion; } }
        public int RemoteResVersion { get { return _remoteResVersion; } }
        public int RemoteAppVersion { get { return _remoteAppVersion; } }

        private string _localVersion;
        private string _remoteVersion;

        private int _localResVersion;
        private int _localAppVersion;
        private int _remoteResVersion;
        private int _remoteAppVersion;
        /// <summary>
        /// 单位字节
        /// </summary>
        private long _downloadSize = 0;
        /// <summary>
        /// 单位字节
        /// </summary>
        private long _totalDownloadSize = 0;


        private List<string> _downloadList = new List<string>();
        private List<string> _redownloadList = new List<string>();

        private readonly ConcurrentQueue<Action> _taskCompleted = new ConcurrentQueue<Action>();
        private ConcurrentQueue<DownloadTask> _taskQueue = new ConcurrentQueue<DownloadTask>();

        private Dictionary<string, AssetInfo> _localMD5Table = new Dictionary<string, AssetInfo>();
        private Dictionary<string, AssetInfo> _remoteMD5Table = new Dictionary<string, AssetInfo>();
        /// <summary>
        /// 当前版本下,持续化目录下载的资源
        /// </summary>
        private HashSet<string> _hasDownload = new HashSet<string>();

        private string _urlRoot = "http://localhost:8086/";
        private readonly object _obj = new object();
        private readonly int _threadNum = 5;
        private int _overThreadNum;

        /// <summary>
        /// 对应资源下载完毕
        /// </summary>
        private bool _downloadOver = true;
        public bool HasdownloadFile { get { return File.Exists(Util.DataPath + ConstSetting.HasDownloadFile); } }

        public void Init()
        {
        }

        public void Dispose()
        {
            _downloadList.Clear();
            _redownloadList.Clear();
            _taskCompleted.Clear();
            _taskQueue.Clear();
            _localMD5Table.Clear();
            _remoteMD5Table.Clear();
            _hasDownload.Clear();
        }

        public void Update()
        {
            while (true)
            {
                Action onComplete;
                if (_taskCompleted.TryDequeue(out onComplete))
                    onComplete();
                else
                    break;
            }
        }

        /// <summary>
        /// 检查资源版本号,判断是否需要更新资源
        /// </summary>
        public IEnumerator CheckVersion()
        {
            Launcher.Ins.SetLaunchState(LaunchState.CheckVersion, 0.2f);
            _localVersion = LoadLocalFile(ConstSetting.ResVersionFile);
            string[] nodes = _localVersion.Split(",".ToCharArray());
            _localAppVersion = Convert.ToInt32(nodes[0]);
            _localResVersion = Convert.ToInt32(nodes[1]);

            _downloadOver = false;
            for (int i = 0; !_downloadOver && i < Client.ServerMgr.UpdateResUrls.Count; i++)
            {
                _urlRoot = Client.ServerMgr.UpdateResUrls[i];
                yield return DwonloadRemoteVersion();
                Launcher.Ins.SetLaunchState(LaunchState.CheckVersion, 0.3f + i * 0.1f);
            }
            if (!_downloadOver)
            {
                Debug.LogError(ConstSetting.ResVersionFile + "下载失败!");
                Launcher.Ins.SetLaunchState(LaunchState.DownloadVersionFailed, 1f);
                yield break;
            }
            Launcher.Ins.SetLaunchState(LaunchState.CheckVersion, 1f);

            if (_localAppVersion < _remoteAppVersion)
            {
                //1.直接下载,退出安装App
                //2.给定链接去下载安装App
                yield break;
            }

            if (_localResVersion >= _remoteResVersion)
                yield break;

            _localMD5Table = PaseMD5Table(LoadLocalFile(ConstSetting.ResMD5File));

            _downloadOver = false;
            for (int i = 0; !_downloadOver && i < Client.ServerMgr.UpdateResUrls.Count; i++)
            {
                _urlRoot = Client.ServerMgr.UpdateResUrls[i];
                yield return DwonloadRemoteMD5Table();
            }
            if (!_downloadOver)
            {
                Debug.LogError(ConstSetting.ResMD5File + "下载失败!");
                Launcher.Ins.SetLaunchState(LaunchState.DownloadMD5TableFailed, 1f);
                yield break;
            }
            string hasDownloadText = LoadLocalFile(ConstSetting.HasDownloadFile);
            string[] files = hasDownloadText.Split("\r\n".ToCharArray());
            _hasDownload = new HashSet<string>(files);

            _downloadOver = false;
            DeleteUselessFiles();
            CollectDownloadList();
            if (!_downloadOver)
            {
                //移动网络时,大于10MB提示是否直接下载
                if (_totalDownloadSize > 10485760)
                {
                    yield return BeginDownloadAssets();
                }
                else
                {
                    yield return BeginDownloadAssets();
                }
            }

            SaveTableFile(_hasDownload, Util.DataPath + ConstSetting.HasDownloadFile);
            SaveTableFile(_remoteMD5Table.Values, Util.DataPath + ConstSetting.ResMD5File);
            File.WriteAllText(Util.DataPath + ConstSetting.ResVersionFile, _remoteVersion);
        }

        IEnumerator BeginDownloadApplication()
        {
            yield return null;
        }
        IEnumerator BeginDownloadAssets()
        {
            ServicePointManager.DefaultConnectionLimit = 50;
            float unit = 1024 * 1024 * 1024;//mb
            float totalSize = _totalDownloadSize / unit;

            Launcher.Ins.SetLaunchState(LaunchState.DownloadHotfixRes, 0f);
            Debug.Log("Begin Download Assets Time = " + Time.realtimeSinceStartup);
            for (int i = 0; !_downloadOver && i < Client.ServerMgr.UpdateResUrls.Count; i++)
            {
                _urlRoot = Client.ServerMgr.UpdateResUrls[i];
                _taskQueue.Clear();
                _redownloadList.Clear();
                for (int j = 0; j < _downloadList.Count; j++)
                {
                    string file = _downloadList[j];
                    string url = GetRemotePath(file);
                    var remoteInfo = _remoteMD5Table[file];
                    url += "?version=" + remoteInfo.MD5;
                    DownloadTask task = new DownloadTask(url, remoteInfo, this);
                    _taskQueue.Enqueue(task);
                }

                if (_taskQueue.Count > 0)
                {
                    _overThreadNum = 0;
                    for (int k = 0; k < 5; ++k)
                    {
                        Thread thread = new Thread(DownloadThreadProc);
                        thread.Name = "DownloadThread:" + k;
                        thread.Start();
                    }

                    while (_overThreadNum < 5 || _taskCompleted.Count > 0)
                    {
                        Launcher.Ins.SetLaunchState(LaunchState.DownloadHotfixRes, _downloadSize * 1f / _totalDownloadSize);
                        Launcher.Ins.SetSubtitle(string.Format("{0:N2}MB/{1:N2}MB", _downloadSize / unit, totalSize));
                        yield return null;
                    }
                }

                if (_redownloadList.Count > 0)
                {
                    _downloadList.Clear();
                    _downloadList.AddRange(_redownloadList.ToArray());
                }
                else
                {
                    Debug.Log("End Download Assets Time = " + Time.realtimeSinceStartup);
                    _downloadOver = true;
                    //下载完毕

                    Launcher.Ins.SetLaunchState(LaunchState.DownloadHotfixRes, 1f);
                    yield break;
                }
            }
        }

        IEnumerator DwonloadRemoteVersion()
        {
            string url = GetRemotePath(ConstSetting.ResVersionFile);
            using (WWW www = new WWW(url))
            {
                yield return www;
                if (www.error != null)
                {
                    _downloadOver = false;
                    yield break;
                }
                _remoteVersion = www.text.Trim();
                string[] nodes = _remoteVersion.Split(",".ToCharArray());
                _remoteAppVersion = Convert.ToInt32(nodes[0]);
                _remoteResVersion = Convert.ToInt32(nodes[1]);
            }
            _downloadOver = true;
        }
        IEnumerator DwonloadRemoteMD5Table()
        {
            string url = GetRemotePath(ConstSetting.ResMD5File);
            using (WWW www = new WWW(url))
            {
                yield return www;
                if (www.error != null)
                {
                    _downloadOver = false;
                    yield break;
                }
                _remoteMD5Table = PaseMD5Table(www.text);
            }
            _downloadOver = true;
        }

        void DownloadThreadProc()
        {
            while (true)
            {
                DownloadTask task = null;
                if (_taskQueue.TryDequeue(out task))
                    task.BeginDownload();
                else
                    break;
            }
            lock (_obj)
            {
                _overThreadNum++;
            }
        }

        string GetRemotePath(string file)
        {
#if UNITY_ANDROID
            string path = string.Format("{0}android/{1}", _urlRoot, file);
#elif UNITY_IPHONE
            string path = string.Format("{0}ios/{1}", _urlRoot, file);
#else
            string path = "";
#endif
            return path;
        }
        string LoadLocalFile(string localFile)
        {
            string file = Util.DataPath + localFile;
            string content = null;
            try
            {
                content = File.ReadAllText(file);
                if (!string.IsNullOrEmpty(content))
                    return content;
            }
            catch (Exception e)
            {
                Debug.LogError(e);
                return "";
            }
            return content;
        }
        Dictionary<string, AssetInfo> PaseMD5Table(string md5Table)
        {
            var table = new Dictionary<string, AssetInfo>();
            string[] lines = md5Table.Split("\r\n".ToCharArray());
            char[] splits = new char[] { ';' };
            for (int i = 0; i < lines.Length; i++)
            {
                string[] nodes = lines[i].Split(splits);
                if (!table.ContainsKey(nodes[0]))
                    table.Add(nodes[0], new AssetInfo()
                    {
                        RelPath = nodes[0],
                        MD5 = nodes[1],
                        Size = long.Parse(nodes[2]),
                    });
            }
            return table;
        }
        void DeleteUselessFiles()
        {
            if (_remoteMD5Table.Count == 0)
                return;

            List<string> delete = new List<string>();
            var iter = _localMD5Table.Keys.GetEnumerator();
            while (iter.MoveNext())
            {
                string key = iter.Current;
                if (_remoteMD5Table.ContainsKey(key))
                    continue;

                if (_hasDownload.Contains(key))
                {
                    _hasDownload.Remove(key);
                    delete.Add(key);
                }
            }

            for (int i = 0; i < delete.Count; i++)
            {
                string file = Util.DataPath + delete[i];
                if (File.Exists(file))
                    File.Delete(file);
            }
        }
        void CollectDownloadList()
        {
            _downloadList.Clear();
            foreach (var key in _remoteMD5Table.Keys)
            {
                AssetInfo info = null;
                _localMD5Table.TryGetValue(key, out info);
                if (info != null)
                {
                    if (info.MD5.Equals(_remoteMD5Table[key].MD5))
                        continue;
                }

                _downloadList.Add(key);
                _totalDownloadSize += _remoteMD5Table[key].Size;
            }

            _downloadOver = _downloadList.Count == 0;
            Debug.Log("DownloadList Count = " + _downloadList.Count + ", Size = " + _totalDownloadSize);
        }
        void SaveTableFile(IEnumerable enumerable, string path)
        {
            var sb = new StringBuilder();
            var iter = enumerable.GetEnumerator();
            while (iter.MoveNext())
                sb.AppendLine(iter.Current.ToString());
            File.WriteAllText(path, sb.ToString());
        }
    }
}