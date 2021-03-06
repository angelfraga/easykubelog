﻿using DirectoryWatcher;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace WatcherFileListClasses
{
    public class FileEntry
    {
        public string FileName { get; set; }
        public IFileSystemWatcherChangeType LastChanges { get; set; }
    }


    public class ThrottleCalls : IDisposable
    {
        public ThrottleCalls(Action callbackInit, int throttlingInMilliseconds)
        {
            _callback = callbackInit;
            _throttlingInMilliseconds = throttlingInMilliseconds;
        }

        public void Call()
        {
            if (_throttlingInMilliseconds <= 0 || stopwatch == null || stopwatch.ElapsedMilliseconds > _throttlingInMilliseconds)
            {
                // Initially calldirectly or if stopwatch is elapsed
                if (_throttlingInMilliseconds > 0)
                    stopwatch = Stopwatch.StartNew();
                _callback();
                return;
            }


            // Check if semaphore is held -> if so currently a task is running
            if (_sem.WaitAsync(0).Result == false)
            {
                var token = source.Token;
                Task.Run(async () =>
                {
                    try
                    {
                        int delay = (int)(_throttlingInMilliseconds - stopwatch.ElapsedMilliseconds);
                        if (delay < 0) delay = 0;
                        await Task.Delay(delay, token);
                    }
                    finally
                    {
                        stopwatch = Stopwatch.StartNew();
                        _sem.Release();
                        try
                        {
                            _callback();
                        }
                        finally
                        {
                        }
                    }
                }, token);
            }
        }

        public void Dispose()
        {
            source.Cancel();
        }

        readonly SemaphoreSlim _sem = new SemaphoreSlim(0);
        readonly CancellationTokenSource source = new CancellationTokenSource();

        Stopwatch stopwatch = null;
        readonly int _throttlingInMilliseconds;
        readonly Action _callback;
    }



    public class WatcherFileList : IDisposable
    {
        FileDirectoryWatcherSettings _fileDirectoryWatcherSettings;
        readonly object syncListAccess = new object();
        readonly IFileSystemWatcher _watcherInterface;
        readonly int _updateRatioInMilliseconds;
        Action<ReadOnlyCollection<FileEntry>> _fileListChangeCallback;
        ThrottleCalls _throttleCalls;
        List<FileEntry> _currentList;
        FileDirectoryWatcher _watcher;


        public WatcherFileList(FileDirectoryWatcherSettings settings = null, IFileSystemWatcher watcherInterface = null, int updateRatioInMilliseconds = 0)
        {
            _fileDirectoryWatcherSettings = settings ?? new FileDirectoryWatcherSettings { UseManualScan = true };
            _watcherInterface = watcherInterface;
            _updateRatioInMilliseconds = updateRatioInMilliseconds;
        }

        public void Start(Action<ReadOnlyCollection<FileEntry>> fileListChangeCallback)
        {
            Start(String.Empty, fileListChangeCallback);
        }


        public void Start(string fileFilter, Action<ReadOnlyCollection<FileEntry>> fileListChangeCallback)
        {
            DiscardOldWatcher();
            _watcher = new FileDirectoryWatcher(_fileDirectoryWatcherSettings, _watcherInterface);
            _watcher.Open(new FilterAndCallbackArgument(fileFilter, Callback));
            _fileListChangeCallback = fileListChangeCallback;
            _throttleCalls = new ThrottleCalls(CallAfterChange, _updateRatioInMilliseconds);
        }


        void CallAfterChange()
        {
            ReadOnlyCollection<FileEntry> list = null;
            lock (syncListAccess)
            {
                list = _currentList.AsReadOnly();
                _currentList = new List<FileEntry>(); // Event if we overwrite the list it's still held by readonly collection 
            }
            _fileListChangeCallback(list);
        }

        void DiscardOldWatcher()
        {
            lock (syncListAccess)
            {
                _throttleCalls?.Dispose();
                _watcher?.Dispose();
                _watcher = null;
                _currentList = new List<FileEntry>();
            }
        }

        void Callback(object sender, WatcherCallbackArgs args)
        {
            lock (syncListAccess)
            {
                bool found = false;
                for (int i = 0; i < _currentList.Count; ++i)
                {

                    if (_currentList[i].FileName == args.FileName)
                    {
                        _currentList[i].LastChanges |= args.ChangeType;
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    _currentList.Add(new FileEntry { FileName = args.FileName, LastChanges = args.ChangeType });
                }

                _throttleCalls.Call();
            }

        }

        public void Dispose()
        {
            DiscardOldWatcher();
        }

    }
}
