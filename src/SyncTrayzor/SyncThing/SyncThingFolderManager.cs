﻿using NLog;
using RestEase;
using SyncTrayzor.SyncThing.ApiClient;
using SyncTrayzor.SyncThing.EventWatcher;
using SyncTrayzor.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace SyncTrayzor.SyncThing
{
    public interface ISyncThingFolderManager
    {
        bool TryFetchById(string folderId, out Folder folder);
        IReadOnlyCollection<Folder> FetchAll();

        event EventHandler FoldersChanged;
        event EventHandler<FolderSyncStateChangedEventArgs> SyncStateChanged;
        event EventHandler<FolderStatusChangedEventArgs> StatusChanged;
        event EventHandler<FolderErrorsChangedEventArgs> FolderErrorsChanged;
    }

    public class SyncThingFolderManager : ISyncThingFolderManager
    {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        private readonly SynchronizedEventDispatcher eventDispatcher;

        private readonly SynchronizedTransientWrapper<ISyncThingApiClient> apiClient;
        private readonly ISyncThingEventWatcher eventWatcher;
        private readonly TimeSpan ignoresFetchTimeout;

        public event EventHandler FoldersChanged;
        public event EventHandler<FolderSyncStateChangedEventArgs> SyncStateChanged;
        public event EventHandler<FolderStatusChangedEventArgs> StatusChanged;
        public event EventHandler<FolderErrorsChangedEventArgs> FolderErrorsChanged;

        // Folders is a ConcurrentDictionary, which suffices for most access
        // However, it is sometimes set outright (in the case of an initial load or refresh), so we need this lock
        // to create a memory barrier. The lock is only used when setting/fetching the field, not when accessing the
        // Folders dictionary itself.
        private readonly object foldersLock = new object();
        private ConcurrentDictionary<string, Folder> _folders = new ConcurrentDictionary<string, Folder>();
        private ConcurrentDictionary<string, Folder> folders
        {
            get { lock (this.foldersLock) { return this._folders; } }
            set { lock (this.foldersLock) { this._folders = value; } }
        }

        public SyncThingFolderManager(
            SynchronizedTransientWrapper<ISyncThingApiClient> apiClient,
            ISyncThingEventWatcher eventWatcher,
            TimeSpan ignoresFetchTimeout)
        {
            this.eventDispatcher = new SynchronizedEventDispatcher(this);
            this.apiClient = apiClient;
            this.ignoresFetchTimeout = ignoresFetchTimeout;

            this.eventWatcher = eventWatcher;
            this.eventWatcher.SyncStateChanged += (o, e) => this.FolderSyncStateChanged(e);
            this.eventWatcher.FolderStatusChanged += (o, e) => this.FolderStatusChanged(e.FolderId, e.FolderStatus);
            this.eventWatcher.ItemStarted += (o, e) => this.ItemStarted(e.Folder, e.Item);
            this.eventWatcher.ItemFinished += (o, e) => this.ItemFinished(e.Folder, e.Item);
            this.eventWatcher.FolderErrorsChanged += (o, e) => this.FolderErrorsChangedEvt(e.FolderId, e.Errors);
        }

        public bool TryFetchById(string folderId, out Folder folder)
        {
            var folders = this.folders;
            if (folders == null)
            {
                folder = null;
                return false;
            }
            else
            {
                return folders.TryGetValue(folderId, out folder);
            }
        }

        public IReadOnlyCollection<Folder> FetchAll()
        {
            var folders = this.folders;
            if (folders == null)
                return null;
            else
                return new List<Folder>(folders.Values).AsReadOnly();
        }

        public async Task ReloadIgnoresAsync(string folderId)
        {
            Folder folder;
            if (!this.folders.TryGetValue(folderId, out folder))
                return;

            var ignores = await this.apiClient.Value.FetchIgnoresAsync(folderId);
            folder.Ignores = new FolderIgnores(ignores.IgnorePatterns, ignores.RegexPatterns);
        }

        public async Task LoadFoldersAsync(Config config, string tilde, CancellationToken cancellationToken)
        {
            var folders = await this.FetchFoldersAsync(config, tilde, cancellationToken);
            this.folders = new ConcurrentDictionary<string, Folder>(folders.Select(x => new KeyValuePair<string, Folder>(x.FolderId, x)));

            this.OnFoldersChanged();
        }

        public async Task ReloadFoldersAsync(Config config, string tilde, CancellationToken cancellationToken)
        {
            var folders = await this.FetchFoldersAsync(config, tilde, cancellationToken);
            var newFolders = new ConcurrentDictionary<string, Folder>();
            var existingFolders = this.folders;

            // Maybe nothing changed?
            if (existingFolders.Values.SequenceEqual(folders))
                return;

            var changeNotifications = new List<Action>();

            // Re-use the existing folder object if possible
            foreach (var folder in folders)
            {
                Folder existingFolder;
                if (existingFolders.TryGetValue(folder.FolderId, out existingFolder))
                {
                    if (existingFolder.SyncState != folder.SyncState)
                    {
                        changeNotifications.Add(() => this.OnSyncStateChanged(folder, existingFolder.SyncState, folder.SyncState));
                        // A sync state change always implies that the status has changed, since the two go together
                        changeNotifications.Add(() => this.OnStatusChanged(folder, folder.Status));
                        existingFolder.SyncState = folder.SyncState;
                    }
                    newFolders[folder.FolderId] = existingFolder;
                }
                else
                {
                    newFolders[folder.FolderId] = folder;
                }
            }

            this.folders = newFolders;
            foreach (var changeNotification in changeNotifications)
            {
                changeNotification();
            }

            this.OnFoldersChanged();
        }

        private async Task<IEnumerable<Folder>> FetchFoldersAsync(Config config, string tilde, CancellationToken cancellationToken)
        {
            // If the folder is invalid for any reason, we'll ignore it.
            // Again, there's the potential for duplicate folder IDs (if the user's been fiddling their config). 
            // In this case, there's nothing really sensible we can do. Just pick one of them :)
            var folderConstructionTasks = config.Folders
                .Where(x => String.IsNullOrWhiteSpace(x.Invalid))
                .DistinctBy(x => x.ID)
                .Select(async folder =>
                {
                    var ignores = await this.FetchFolderIgnoresAsync(folder.ID, cancellationToken);
                    var status = await this.FetchFolderStatusAsync(folder.ID, cancellationToken);
                    var path = folder.Path;
                    if (path.StartsWith("~"))
                        path = Path.Combine(tilde, path.Substring(1).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

                    return new Folder(folder.ID, path, SyncStateFromStatus(status), new FolderIgnores(ignores.IgnorePatterns, ignores.RegexPatterns), status);
                });

            cancellationToken.ThrowIfCancellationRequested();

            var folders = await Task.WhenAll(folderConstructionTasks);
            return folders;
        }

        private static FolderSyncState SyncStateFromStatus(FolderStatus status)
        {
            return status.State == "syncing" ? FolderSyncState.Syncing : FolderSyncState.Idle;
        }

        private async Task<Ignores> FetchFolderIgnoresAsync(string folderId, CancellationToken cancellationToken)
        {
            // Until startup is complete, these can return a 500.
            // There's no sensible way to determine when startup *is* complete, so we just have to keep trying...

            // Again, there's the possibility that we've just abort the API...
            ISyncThingApiClient apiClient;
            lock (this.apiClient.LockObject)
            {
                cancellationToken.ThrowIfCancellationRequested();
                apiClient = this.apiClient.UnsynchronizedValue;
                if (apiClient == null)
                    throw new InvalidOperationException("ApiClient must not be null");
            }

            Ignores ignores = null;
            // We used to time out after an absolute time here. However, there's the possiblity of going to sleep
            // halfway through polling, which throws things off. Therefore use a number of iterations
            var numRetries = this.ignoresFetchTimeout.TotalSeconds; // Each iteration is a second
            for (var retriesCount = 0; retriesCount < numRetries; retriesCount++)
            {
                try
                {
                    ignores = await apiClient.FetchIgnoresAsync(folderId);
                    // No need to log: ApiClient did that for us
                    break;
                }
                catch (ApiException e)
                {
                    logger.Debug("Attempting to fetch folder {0}, but received status {1}", folderId, e.StatusCode);
                    if (e.StatusCode != HttpStatusCode.InternalServerError)
                        throw;
                }

                await Task.Delay(1000, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (ignores == null)
                throw new SyncThingDidNotStartCorrectlyException($"Unable to fetch ignores for folder {folderId}. Syncthing returned 500 after {this.ignoresFetchTimeout}");

            return ignores;
        }

        private async Task<FolderStatus> FetchFolderStatusAsync(string folderId, CancellationToken cancellationToken)
        {
            var status = await this.apiClient.Value.FetchFolderStatusAsync(folderId, cancellationToken);
            return status;
        }

        private void ItemStarted(string folderId, string item)
        {
            Folder folder;
            if (!this.folders.TryGetValue(folderId, out folder))
                return; // Don't know about it

            folder.AddSyncingPath(item);
        }

        private void ItemFinished(string folderId, string item)
        {
            Folder folder;
            if (!this.folders.TryGetValue(folderId, out folder))
                return; // Don't know about it

            folder.RemoveSyncingPath(item);
        }

        private void FolderErrorsChangedEvt(string folderId, List<FolderErrorData> errors)
        {
            Folder folder;
            if (!this.folders.TryGetValue(folderId, out folder))
                return; // Don't know about it

            var folderErrors = errors.Select(x => new FolderError(x.Error, x.Path)).ToList();
            folder.SetFolderErrors(folderErrors);
            this.OnFolderErrorsChanged(folder, folderErrors);
        }

        private void FolderSyncStateChanged(SyncStateChangedEventArgs e)
        {
            Folder folder;
            if (!this.folders.TryGetValue(e.FolderId, out folder))
                return; // We don't know about this folder

            folder.SyncState = e.SyncState;

            if (e.SyncState == FolderSyncState.Syncing)
            {
                folder.ClearFolderErrors();
                this.OnFolderErrorsChanged(folder, new List<FolderError>());
            }

            this.OnSyncStateChanged(folder, e.PrevSyncState, e.SyncState);
        }

        private void FolderStatusChanged(string folderId, FolderStatus folderStatus)
        {
            Folder folder;
            if (!this.folders.TryGetValue(folderId, out folder))
                return; // Don't know about it

            folder.Status = folderStatus;

            this.OnStatusChanged(folder, folderStatus);
        }

        private void OnFoldersChanged()
        {
            this.eventDispatcher.Raise(this.FoldersChanged);
        }

        private void OnSyncStateChanged(Folder folder, FolderSyncState prevSyncState, FolderSyncState newSyncState)
        {
            this.eventDispatcher.Raise(this.SyncStateChanged, new FolderSyncStateChangedEventArgs(folder.FolderId, prevSyncState, newSyncState));
        }

        private void OnStatusChanged(Folder folder, FolderStatus folderStatus)
        {
            this.eventDispatcher.Raise(this.StatusChanged, new FolderStatusChangedEventArgs(folder.FolderId, folderStatus));
        }

        private void OnFolderErrorsChanged(Folder folder, List<FolderError> folderErrors)
        {
            this.eventDispatcher.Raise(this.FolderErrorsChanged, new FolderErrorsChangedEventArgs(folder.FolderId, folderErrors));
        }
    }
}
