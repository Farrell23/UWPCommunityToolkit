﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Microsoft.Toolkit.Uwp.UI.Cache
{
    /// <summary>
    /// Provides methods and tools to cache files in a folder
    /// </summary>
    public class FileCache<T>
    {
        private class ConcurrentRequest
        {
            public Task<T> Task { get; set; }

            public bool EnsureCachedCopy { get; set; }
        }

        private readonly SemaphoreSlim _cacheFolderSemaphore = new SemaphoreSlim(1);

        private StorageFolder _baseFolder = null;
        private string _cacheFolderName = null;

        private StorageFolder _cacheFolder = null;
        private InMemoryStorage<T> _inMemoryFileStorage = null;

        private Dictionary<string, ConcurrentRequest> _concurrentTasks = new Dictionary<string, ConcurrentRequest>();
        private object _concurrencyLock = new object();

        static FileCache()
        {
            FileCacheInstance = new FileCache<T>()
            {
                InternalCacheDuration = TimeSpan.FromDays(1)
            };
        }

        /// <summary>
        /// Gets instance of FileCache. Exposing it as static property will allow inhertance and polymorphism while
        /// exposing the underlying object and its functionality through this property,
        /// </summary>
        public static FileCache<T> FileCacheInstance { get; private set; }

        /// <summary>
        /// Gets or sets Cache duration. This delegates the call to instance member.
        /// </summary>
        [Obsolete("This property is obselete. Please use FileCacheInstance.InternalCacheDuration")]
        public static TimeSpan CacheDuration
        {
            get
            {
                return FileCacheInstance.InternalCacheDuration;
            }

            set
            {
                FileCacheInstance.InternalCacheDuration = value;
            }
        }

        /// <summary>
        /// Gets or sets the life duration of every cache entry.
        /// </summary>
        public TimeSpan InternalCacheDuration { get; set; }

        /// <summary>
        /// Initialises FileCache and provides root folder and cache folder name
        /// </summary>
        /// <param name="folder">Folder that is used as root for cache</param>
        /// <param name="folderName">Cache folder name</param>
        /// <returns>awaitable task</returns>
        public virtual async Task InitialiseAsync(StorageFolder folder, string folderName)
        {
            _baseFolder = folder;
            _cacheFolderName = folderName;

            _cacheFolder = await GetCacheFolderAsync();
        }

        /// <summary>
        /// Clears all files in the cache
        /// </summary>
        /// <returns>awaitable task</returns>
        public async Task ClearAsync()
        {
            var folder = await GetCacheFolderAsync();
            var files = await folder.GetFilesAsync();

            await InternalClearAsync(files);
        }

        /// <summary>
        /// Clears file if it has expired
        /// </summary>
        /// <param name="duration">timespan to compute whether file has expired or not</param>
        /// <returns>awaitable task</returns>
        public async Task ClearAsync(TimeSpan duration)
        {
            DateTime expirationDate = DateTime.Now.Subtract(duration);

            var folder = await GetCacheFolderAsync();
            var files = await folder.GetFilesAsync();

            var filesToDelete = new List<StorageFile>();

            foreach (var file in files)
            {
                if (await IsFileOutOfDate(file, expirationDate))
                {
                    filesToDelete.Add(file);
                }
            }

            await InternalClearAsync(files);
        }

        private async Task<T> GetItemAsync(Uri uri, string fileName, bool throwOnError, bool preCacheOnly)
        {
            T t = default(T);

            ConcurrentRequest request;

            lock (_concurrencyLock)
            {
                if (_concurrentTasks.ContainsKey(fileName))
                {
                    request = _concurrentTasks[fileName];
                }
                else
                {
                    request = new ConcurrentRequest()
                    {
                        Task = GetFromCacheOrDownloadAsync(uri, fileName, preCacheOnly),
                        EnsureCachedCopy = preCacheOnly
                    };
                    _concurrentTasks.Add(fileName, request);
                }
            }

            try
            {
                t = await request.Task;

                // if task was "PreCache task" and we needed "Get task" and task didnt return image we create new "Get task" and await on it.
                if (request.EnsureCachedCopy && !preCacheOnly && t.Equals(default(T)))
                {
                    lock (_concurrentTasks)
                    {
                        request = new ConcurrentRequest()
                        {
                            Task = GetFromCacheOrDownloadAsync(uri, fileName, preCacheOnly),
                            EnsureCachedCopy = preCacheOnly
                        };
                        _concurrentTasks[fileName] = request;
                    }

                    t = await request.Task;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex.Message);
                if (throwOnError)
                {
                    throw ex;
                }
            }
            finally
            {
                lock (_concurrencyLock)
                {
                    if (_concurrentTasks.ContainsKey(fileName))
                    {
                        _concurrentTasks.Remove(fileName);
                    }
                }
            }

            return t;
        }

        private async Task<T> GetFromCacheOrDownloadAsync(Uri uri, string fileName, bool preCacheOnly)
        {
            StorageFile baseFile = null;
            T t = default(T);
            DateTime expirationDate = DateTime.Now.Subtract(InternalCacheDuration);

            if (_inMemoryFileStorage.MaxItemCount > 0)
            {
                var msi = _inMemoryFileStorage.GetItem(fileName, InternalCacheDuration);
                if (msi != null)
                {
                    t = msi.Item;
                }
            }

            if (t != null)
            {
                return t;
            }

            var folder = await GetCacheFolderAsync();

            baseFile = await folder.TryGetItemAsync(fileName) as StorageFile;
            if (await IsFileOutOfDate(baseFile, expirationDate))
            {
                baseFile = await folder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
                try
                {
                    t = await DownloadFile(uri, baseFile, preCacheOnly);
                }
                catch (Exception)
                {
                    await baseFile.DeleteAsync();
                    throw; // rethrowing the exception changes the stack trace. just throw
                }
            }

            if (t.Equals(default(T)) && !preCacheOnly)
            {
                using (var fileStream = await baseFile.OpenAsync(FileAccessMode.Read))
                {
                    t = await InitialiseType(fileStream);
                }

                if (_inMemoryFileStorage.MaxItemCount > 0)
                {
                    var properties = await baseFile.GetBasicPropertiesAsync();

                    var msi = new InMemoryStorageItem<T>(fileName, properties.DateModified.DateTime, t);
                    _inMemoryFileStorage.SetItem(msi);
                }
            }

            return t;
        }

        /// <summary>
        /// Cache specific hooks to proccess items from http response
        /// </summary>
        /// <param name="webStream">http reposonse stream</param>
        /// <returns>awaitable task</returns>
        protected virtual async Task<T> InitialiseType(IRandomAccessStream webStream)
        {
            // nothing to do in this instance;
            return default(T);
        }

        /// <summary>
        /// Cache specific hooks to proccess items from http response
        /// </summary>
        /// <param name="baseFile">storage file</param>
        /// <returns>awaitable task</returns>
        protected virtual async Task<T> InitialiseType(StorageFile baseFile)
        {
            // nothing to do in this instance;
            //if (typeof(T) == typeof(StorageFile))
            //{
            //    return (T)Convert.ChangeType(baseFile, typeof(T));
            //}

            return default(T);
        }

        private async Task<T> DownloadFile(Uri uri, StorageFile baseFile, bool preCacheOnly)
        {
            T t = default(T);

            using (var webStream = await StreamHelper.GetHttpStreamAsync(uri))
            {
                // if its pre-cache we aren't looking to load items in memory
                if (!preCacheOnly)
                {
                    t = await InitialiseType(webStream);

                    webStream.Seek(0);
                }

                using (var reader = new DataReader(webStream))
                {
                    await reader.LoadAsync((uint)webStream.Size);
                    var buffer = new byte[(int)webStream.Size];
                    reader.ReadBytes(buffer);
                    await FileIO.WriteBytesAsync(baseFile, buffer);
                }
            }

            return t;
        }

        private async Task<bool> IsFileOutOfDate(StorageFile file, DateTime expirationDate)
        {
            if (file == null)
            {
                return true;
            }

            var properties = await file.GetBasicPropertiesAsync();
            return properties.DateModified < expirationDate;
        }

        private async Task InternalClearAsync(IEnumerable<StorageFile> files)
        {
            foreach (var file in files)
            {
                try
                {
                    await file.DeleteAsync();
                }
                catch
                {
                    // Just ignore errors for now}
                }
            }
        }

        /// <summary>
        /// Initialises with default values if user has not initialised explicitly
        /// </summary>
        /// <returns>awaitable task</returns>
        private async Task ForceInitialiseAsync()
        {
            if (_cacheFolder != null)
            {
                return;
            }

            await _cacheFolderSemaphore.WaitAsync();

            _inMemoryFileStorage = new InMemoryStorage<T>();

            if (_baseFolder == null)
            {
                _baseFolder = ApplicationData.Current.TemporaryFolder;
            }

            if (string.IsNullOrWhiteSpace(_cacheFolderName))
            {
                _cacheFolderName = GetType().Name;
            }

            try
            {
                _cacheFolder = await ApplicationData.Current.TemporaryFolder.CreateFolderAsync(_cacheFolderName, CreationCollisionOption.OpenIfExists);
            }
            finally
            {
                _cacheFolderSemaphore.Release();
            }
        }

        private async Task<StorageFolder> GetCacheFolderAsync()
        {
            if (_cacheFolder == null)
            {
                await ForceInitialiseAsync();
            }

            return _cacheFolder;
        }
    }
}