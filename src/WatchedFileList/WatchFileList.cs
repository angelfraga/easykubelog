﻿using DirectoryWatching;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WatchedFileList
{
    using FileListType = ReadOnlyCollection<FileEntry>;

    public class FileEntry
    {
        public string FileName { get; set; }
        public IFileSystemWatcherChangeType LastChanges { get; set; }
    }


    public class ThrottleCalls :  IDisposable 
    {
        public ThrottleCalls(Action callbackInit, int throttlingInMilliseconds)
        {
            _callback = callbackInit;
            _throttlingInMilliseconds = throttlingInMilliseconds;
        }

        public void Call()
        {
            if (stopwatch == null || stopwatch.ElapsedMilliseconds > _throttlingInMilliseconds)
            {
                // Initially calldirectly or if stopwatch is elapsed
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

        SemaphoreSlim _sem = new SemaphoreSlim(0);
        CancellationTokenSource source = new CancellationTokenSource();

        Stopwatch stopwatch = null;
        readonly int _throttlingInMilliseconds;
        readonly Action _callback;
    }


    public class WatchFileList
    {
        public WatchFileList(string directoryToWatch, IFileSystemWatcher watcherInterface = null, int updateRatioInMilliseconds = 0)
        {
            _watcherInterface = watcherInterface;
            _directoryToWatch = directoryToWatch;
            _updateRatioInMilliseconds = updateRatioInMilliseconds;
        }

        public void Start(Action<ReadOnlyCollection<FileEntry>> fileListChangeCallback)
        {
            Start(String.Empty, fileListChangeCallback);
        }


        public void Start(string fileFilter, Action<ReadOnlyCollection<FileEntry>> fileListChangeCallback)
        {
            DiscardOldWatcher();
            lastCall = null;
            _watcher = new DirectoryWatcher(_watcherInterface);
            _watcher.Open(_directoryToWatch, new FilterAndCallbackArgument(fileFilter, Callback));
            _fileListChangeCallback = fileListChangeCallback;
            _throttleCalls = new ThrottleCalls(CallAfterChange, _updateRatioInMilliseconds);

        }

        void CallAfterChange()
        {
            ReadOnlyCollection<FileEntry> list = null;
            lock (syncListAccess)
            {
                list = currentList.AsReadOnly();
                currentList = new List<FileEntry>(); // Event if we overwrite the list it's still held by readonly collection 
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
                currentList = new List<FileEntry>();                
            }
        }

        void Callback(object sender, WatcherCallbackArgs args)
        {
            lock (syncListAccess)
            {
                bool found = false;
                for (int i = 0; i < currentList.Count; ++i)
                {
                    
                    if (currentList[i].FileName == args.Name)
                    {
                        currentList[i].LastChanges |= args.ChangeType;
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    currentList.Add(new FileEntry {FileName = args.Name, LastChanges = args.ChangeType });
                }

                _throttleCalls.Call();
            }

        }

        object syncListAccess = new object();
        
        List<FileEntry> currentList;

        Stopwatch lastCall = null;
        DirectoryWatcher _watcher;
        IFileSystemWatcher _watcherInterface;
        string _directoryToWatch;
        int _updateRatioInMilliseconds;
        Action<ReadOnlyCollection<FileEntry>>  _fileListChangeCallback;
        ThrottleCalls _throttleCalls;
    }
}
