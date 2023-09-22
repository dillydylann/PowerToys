﻿// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.Win32;
using Peek.Common.Extensions;
using Peek.Common.Helpers;
using Peek.Common.Models;
using Peek.FilePreviewer.Models;
using Peek.FilePreviewer.Previewers.Helpers;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.Shell.PropertiesSystem;

namespace Peek.FilePreviewer.Previewers
{
    public partial class ShellPreviewHandlerPreviewer : ObservableObject, IShellPreviewHandlerPreviewer, IDisposable
    {
        [ObservableProperty]
        private IPreviewHandler? preview;

        [ObservableProperty]
        private PreviewState state;

        private Stream? fileStream;

        public ShellPreviewHandlerPreviewer(IFileSystemItem file)
        {
            FileItem = file;
            Dispatcher = DispatcherQueue.GetForCurrentThread();
        }

        private IFileSystemItem FileItem { get; }

        private DispatcherQueue Dispatcher { get; }

        public void Dispose()
        {
            Clear();
            GC.SuppressFinalize(this);
        }

        public async Task CopyAsync()
        {
            await Dispatcher.RunOnUiThread(async () =>
            {
                var storageItem = await FileItem.GetStorageItemAsync();
                ClipboardHelper.SaveToClipboard(storageItem);
            });
        }

        public Task<PreviewSize> GetPreviewSizeAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(new PreviewSize { MonitorSize = null });
        }

        public async Task LoadPreviewAsync(CancellationToken cancellationToken)
        {
            Clear();
            State = PreviewState.Loading;

            cancellationToken.ThrowIfCancellationRequested();

            // Create the preview handler
            var previewHandler = await Task.Run(() =>
            {
                var previewHandlerGuid = GetPreviewHandlerGuid(FileItem.Extension);
                if (!string.IsNullOrEmpty(previewHandlerGuid))
                {
                    return Activator.CreateInstance(Type.GetTypeFromCLSID(Guid.Parse(previewHandlerGuid))!) as IPreviewHandler;
                }
                else
                {
                    return null;
                }
            });

            if (previewHandler == null)
            {
                State = PreviewState.Error;
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Initialize the preview handler with the selected file
            bool success = await Task.Run(() =>
            {
                const uint STGM_READ = 0x00000000;
                if (previewHandler is IInitializeWithStream initWithStream)
                {
                    fileStream = File.OpenRead(FileItem.Path);
                    initWithStream.Initialize(new IStreamWrapper(fileStream), STGM_READ);
                }
                else if (previewHandler is IInitializeWithFile initWithFile)
                {
                    unsafe
                    {
                        fixed (char* pPath = FileItem.Path)
                        {
                            initWithFile.Initialize(pPath, STGM_READ);
                        }
                    }
                }
                else
                {
                    // Handler is missing the required interfaces
                    return false;
                }

                return true;
            });

            if (!success)
            {
                State = PreviewState.Error;
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Preview.SetWindow() needs to be set in the control
            Preview = previewHandler;
            State = PreviewState.Loaded;
        }

        public void Clear()
        {
            if (Preview != null)
            {
                try
                {
                    Preview.Unload();
                    Marshal.FinalReleaseComObject(Preview);
                }
                catch
                {
                }

                Preview = null;
            }

            if (fileStream != null)
            {
                fileStream.Dispose();
                fileStream = null;
            }
        }

        public static bool IsFileTypeSupported(string fileExt)
        {
            return !string.IsNullOrEmpty(GetPreviewHandlerGuid(fileExt));
        }

        private static string? GetPreviewHandlerGuid(string fileExt)
        {
            const string PreviewHandlerKeyPath = "shellex\\{8895b1c6-b41f-4c1c-a562-0d564250836f}";

            // Search by file extension
            using var classExtensionKey = Registry.ClassesRoot.OpenSubKey(fileExt);
            using var classExtensionPreviewHandlerKey = classExtensionKey?.OpenSubKey(PreviewHandlerKeyPath);

            if (classExtensionKey != null && classExtensionPreviewHandlerKey == null)
            {
                // Search by file class
                var className = classExtensionKey.GetValue(null) as string;
                if (!string.IsNullOrEmpty(className))
                {
                    using var classKey = Registry.ClassesRoot.OpenSubKey(className);
                    using var classPreviewHandlerKey = classKey?.OpenSubKey(PreviewHandlerKeyPath);

                    return classPreviewHandlerKey?.GetValue(null) as string;
                }
            }

            return classExtensionPreviewHandlerKey?.GetValue(null) as string;
        }
    }
}