using log4net;
using Renci.SshNet;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;

namespace CustomsReceipt2GI
{
    public partial class WatcherService : ServiceBase
    {

        FileSystemWatcher watcher;
        string _Filter, _WatcherPath, _KeepPath, _SFTPUsername, _SFTPPassword, _SFTPWorkingPath, _SFTPHost, _SFTPPort;
        private ReaderWriterLockSlim rwlock;
        private System.Timers.Timer processTimer;
        private List<string> workingFileList;
        private SftpClient sftpClient;
        private Thread worker_one = null;
        private static readonly ILog log = LogManager.GetLogger
                (System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public WatcherService()
        {
            InitializeComponent();
            _Filter = ConfigurationManager.AppSettings.Get("_Filter");
            _WatcherPath = ConfigurationManager.AppSettings.Get("_WatcherPath");
            _KeepPath = ConfigurationManager.AppSettings.Get("_KeepPath");
            _SFTPUsername = ConfigurationManager.AppSettings.Get("_SFTPUsername");
            _SFTPPassword = ConfigurationManager.AppSettings.Get("_SFTPPassword");
            _SFTPWorkingPath = ConfigurationManager.AppSettings.Get("_SFTPWorkingPath");
            _SFTPHost = ConfigurationManager.AppSettings.Get("_SFTPHost");
            _SFTPPort = ConfigurationManager.AppSettings.Get("_SFTPPort");
            workingFileList = new List<string>();
            rwlock = new ReaderWriterLockSlim();
            log.Debug("WatcherService# Constructor");
        }

        protected override void OnStart(string[] args)
        {
            log.Debug("WatcherService# Start Service");
            ThreadStart threadStart_one = new ThreadStart(InitFileSystemWatcher);
            worker_one = new Thread(threadStart_one);
            worker_one.Start();
        }

        protected override void OnStop()
        {
            try
            {
                if (worker_one != null & worker_one.IsAlive)
                {
                    worker_one.Abort();
                }
            }
            catch (Exception)
            {
                throw;
            }
        }

        private void InitFileSystemWatcher()
        {
            log.Debug("WatcherService# InitFileSystemWatcher");
            sftpClient = new SftpClient(_SFTPHost, _SFTPUsername, _SFTPPassword);
            sftpClient.Connect();
            watcher = new FileSystemWatcher();
            watcher.Filter = _Filter;
            watcher.Path = _WatcherPath;
            watcher.NotifyFilter = NotifyFilters.LastWrite;
            watcher.EnableRaisingEvents = true;
            watcher.IncludeSubdirectories = true;
            watcher.Error += Watcher_Error;
            watcher.Changed += new FileSystemEventHandler(FileChanged);
            log.Debug("WatcherService# FinishInitFileSystemWatcher");
        }

        private void Watcher_Error(object sender, ErrorEventArgs e)
        {
            // Watcher crashed. Re-init.
            InitFileSystemWatcher();
            log.Error("WatcherService# " + e.GetException().Message);
        }

        private void FileChanged(object sender, FileSystemEventArgs e)
        {
            try
            {
                rwlock.EnterReadLock();
                workingFileList.Add(e.FullPath);
                if (processTimer == null)
                {
                    // First file, start timer.
                    processTimer = new System.Timers.Timer(2000);
                    processTimer.Elapsed += ProcessQueue;
                    processTimer.Start();
                }
                else
                {
                    // Subsequent file, reset timer.
                    processTimer.Stop();
                    processTimer.Start();
                }
            }
            finally
            {
                rwlock.ExitReadLock();
            }
        }

        private void ProcessQueue(object sender, ElapsedEventArgs args)
        {
            try
            {
                rwlock.EnterReadLock();
                foreach (string filePath in workingFileList)
                {
                    log.Debug("WatcherService# > ProcessQueue# " + filePath);
                    using (Stream inputStream = new FileStream(filePath, FileMode.Open))
                    {
                        sftpClient.UploadFile(inputStream, _SFTPWorkingPath + Path.GetFileName(filePath));
                        log.Debug("WatcherService# > ProcessQueue# Transfer to sftp [" + filePath + "]");
                    }
                    File.Move(filePath, _KeepPath);
                    log.Debug("WatcherService# > ProcessQueue# Transfer to keep folder [" + filePath + "]");
                    File.Delete(filePath);
                }
                workingFileList.Clear();
            }
            finally
            {
                if (processTimer != null)
                {
                    processTimer.Stop();
                    processTimer.Dispose();
                    processTimer = null;
                }
                rwlock.ExitReadLock();
            }
        }
    }
}
