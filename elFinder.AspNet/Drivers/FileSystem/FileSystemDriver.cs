﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using elFinder.AspNet.Drawing;
using elFinder.AspNet.Helpers;
using elFinder.AspNet.Models;
using elFinder.AspNet.Models.Commands;

namespace elFinder.AspNet.Drivers.FileSystem
{
    /// <summary>
    /// Represents a driver for local file system
    /// </summary>
    public class FileSystemDriver : BaseDriver, IDriver
    {
        private const string _volumePrefix = "v";

        #region Constructor

        /// <summary>
        /// Initialize new instance of class ElFinder.FileSystemDriver
        /// </summary>
        public FileSystemDriver()
        {
            VolumePrefix = _volumePrefix;
            Roots = new List<RootVolume>();
        }

        #endregion Constructor

        #region IDriver Members

        public async Task<ConnectorResult> ArchiveAsync(FullPath parentPath, IEnumerable<FullPath> paths, string filename, string mimeType)
        {
            var response = new AddResponseModel();

            if (paths == null)
            {
                throw new NotSupportedException();
            }

            if (mimeType != "application/zip")
            {
                throw new NotSupportedException("Only .zip files are currently supported.");
            }

            // Parse target path

            var directoryInfo = parentPath.Directory;

            if (directoryInfo != null)
            {
                if (filename is null)
                {
                    filename = "newfile";
                }

                if (filename.EndsWith(".zip"))
                {
                    filename = filename.Replace(".zip", "");
                }

                string newPath = Path.Combine(directoryInfo.FullName, filename + ".zip");

                if (File.Exists(newPath))
                {
                    File.Delete(newPath);
                }

                using (var newFile = ZipFile.Open(newPath, ZipArchiveMode.Create))
                {
                    foreach (var tg in paths)
                    {
                        if (tg.IsDirectory)
                        {
                            await AddDirectoryToArchiveAsync(newFile, tg.Directory, "");
                        }
                        else
                        {
                            newFile.CreateEntryFromFile(tg.File.FullName, tg.File.Name);
                        }
                    }
                }

                response.Added.Add(await BaseModel.CreateAsync(new FileSystemFile(newPath), parentPath.RootVolume));
            }

            return new ConnectorResult(response);
        }

        public async Task<ConnectorResult> CropAsync(FullPath path, int x, int y, int width, int height)
        {
            await RemoveThumbsAsync(path);

            // Crop Image
            ImageWithMimeType image;
            using (var stream = new FileStream(path.File.FullName, FileMode.Open))
            {
                image = path.RootVolume.PictureEditor.Crop(stream, x, y, width, height);
            }

            using (var fileStream = File.Create(path.File.FullName))
            {
                await image.ImageStream.CopyToAsync(fileStream);
            }

            var output = new ChangedResponseModel();
            output.Changed.Add(await BaseModel.CreateAsync(path.File, path.RootVolume));
            return new ConnectorResult(output);
        }

        public Task<ConnectorResult> DimAsync(FullPath path)
        {
            using (var stream = new FileStream(path.File.FullName, FileMode.Open))
            {
                var response = new DimResponseModel(path.RootVolume.PictureEditor.ImageSize(stream));
                return Task.FromResult(new ConnectorResult(response));
            }
        }

        public async Task<ConnectorResult> DuplicateAsync(IEnumerable<FullPath> paths)
        {
            var response = new AddResponseModel();
            foreach (var path in paths)
            {
                if (path.IsDirectory)
                {
                    string parentPath = path.Directory.Parent.FullName;
                    string name = path.Directory.Name;
                    string newName = $"{parentPath}{Path.DirectorySeparatorChar}{name} copy";

                    if (!Directory.Exists(newName))
                    {
                        DirectoryCopy(path.Directory.FullName, newName, true);
                    }
                    else
                    {
                        bool foundNewName = false;
                        for (int i = 1; i < 100; i++)
                        {
                            newName = $"{parentPath}{Path.DirectorySeparatorChar}{name} copy {i}";
                            if (!Directory.Exists(newName))
                            {
                                DirectoryCopy(path.Directory.FullName, newName, true);
                                foundNewName = true;
                                break;
                            }
                        }

                        if (!foundNewName)
                        {
                            return new ConnectorResult($"Unable to create new file with name {parentPath}{Path.DirectorySeparatorChar}{name} copy");
                        }
                    }

                    response.Added.Add(await BaseModel.CreateAsync(new FileSystemDirectory(newName), path.RootVolume));
                }
                else
                {
                    string parentPath = path.File.Directory.FullName;
                    string name = path.File.Name.Substring(0, path.File.Name.Length - path.File.Extension.Length);
                    string ext = path.File.Extension;

                    string newName = $"{parentPath}{Path.DirectorySeparatorChar}{name} copy{ext}";

                    if (!File.Exists(newName))
                    {
                        File.Copy(path.File.FullName, newName);
                    }
                    else
                    {
                        bool foundNewName = false;
                        for (int i = 1; i < 100; i++)
                        {
                            newName = $"{parentPath}{Path.DirectorySeparatorChar}{name} copy {i}{ext}";
                            if (!File.Exists(newName))
                            {
                                File.Copy(path.File.FullName, newName);
                                foundNewName = true;
                                break;
                            }
                        }

                        if (!foundNewName)
                        {
                            return new ConnectorResult($"Unable to create new file with name {parentPath}{Path.DirectorySeparatorChar}{name} copy{ext}");
                        }
                    }
                    response.Added.Add(await BaseModel.CreateAsync(new FileSystemFile(newName), path.RootVolume));
                }
            }
            return new ConnectorResult(response);
        }

        public async Task<ConnectorResult> ExtractAsync(FullPath fullPath, bool newFolder)
        {
            var response = new AddResponseModel();

            if (fullPath.IsDirectory || fullPath.File.Extension.ToLower() != ".zip")
            {
                throw new NotSupportedException("Only .zip files are currently supported.");
            }

            string rootPath = fullPath.File.Directory.FullName;

            if (newFolder)
            {
                rootPath = Path.Combine(rootPath, Path.GetFileNameWithoutExtension(fullPath.File.Name));
                var rootDir = new FileSystemDirectory(rootPath);
                if (!await rootDir.ExistsAsync)
                {
                    await rootDir.CreateAsync();
                }
                response.Added.Add(await BaseModel.CreateAsync(rootDir, fullPath.RootVolume));
            }

            using (var archive = ZipFile.OpenRead(fullPath.File.FullName))
            {
                string separator = Path.DirectorySeparatorChar.ToString();
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    try
                    {
                        //Replce zip entry path separator by system path separator
                        string file = Path.Combine(rootPath, entry.FullName)
                             .Replace("/", separator).Replace("\\", separator);

                        string destPath = Path.GetFullPath(file);
                        if (!destPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new NotSupportedException($"Entry '{entry.FullName}' is outside of the destination directory.");
                        }

                        if (file.EndsWith(separator)) //directory
                        {
                            var dir = new FileSystemDirectory(file);

                            if (!await dir.ExistsAsync)
                            {
                                await dir.CreateAsync();
                            }
                            if (!newFolder)
                            {
                                response.Added.Add(await BaseModel.CreateAsync(dir, fullPath.RootVolume));
                            }
                        }
                        else
                        {
                            entry.ExtractToFile(file, true);
                            if (!newFolder)
                            {
                                response.Added.Add(await BaseModel.CreateAsync(new FileSystemFile(file), fullPath.RootVolume));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Trace.TraceError("{0} - {1}", entry.FullName, ex.Message);
                    }
                }
            }

            return new ConnectorResult(response);
        }

        public async Task<ConnectorResult> FileAsync(FullPath path, bool download)
        {
            FileContent result;

            if (path.IsDirectory)
            {
                return new ConnectorResult("errNotFile");
            }

            if (!await path.File.ExistsAsync)
            {
                return new ConnectorResult("errFileNotFound");
            }

            if (path.RootVolume.IsShowOnly)
            {
                return new ConnectorResult("errPerm");
            }

            result = new FileContent {
                FileName = path.File.FullName,
                Length = await path.File.LengthAsync,
                ContentStream = File.OpenRead(path.File.FullName),
                ContentType = download ? "application/octet-stream" : MimeHelper.GetMimeType(path.File.Extension)
            };

            return new ConnectorResult(result);
        }

        public async Task<ConnectorResult> GetAsync(FullPath path)
        {
            var response = new GetResponseModel();
            using (var reader = new StreamReader(await path.File.OpenReadAsync()))
            {
                response.Content = reader.ReadToEnd();
            }
            return new ConnectorResult(response);
        }

        public async Task<ConnectorResult> InitAsync(FullPath path, IEnumerable<string> mimeTypes)
        {
            if (path == null)
            {
                var root = Roots.FirstOrDefault(r => r.IsStartingVolume);
                if (root == null)
                {
                    root = Roots.First();
                }

                path = new FullPath(root, new FileSystemDirectory(root.StartDirectory ?? root.RootDirectory), null);
            }

            var response = new InitResponseModel(await BaseModel.CreateAsync(path.Directory, path.RootVolume), new Options(path));

            foreach (var item in await path.Directory.GetFilesAsync(mimeTypes))
            {
                if (!item.Attributes.HasFlag(FileAttributes.Hidden))
                {
                    response.Files.Add(await BaseModel.CreateAsync(item, path.RootVolume));
                }
            }
            foreach (var item in await path.Directory.GetDirectoriesAsync())
            {
                if (!item.Attributes.HasFlag(FileAttributes.Hidden))
                {
                    response.Files.Add(await BaseModel.CreateAsync(item, path.RootVolume));
                }
            }

            foreach (var item in Roots)
            {
                response.Files.Add(await BaseModel.CreateAsync(new FileSystemDirectory(item.RootDirectory), item));
            }

            if (path.RootVolume.RootDirectory != path.Directory.FullName)
            {
                var dirInfo = new DirectoryInfo(path.RootVolume.RootDirectory);
                foreach (var item in dirInfo.GetDirectories())
                {
                    var attributes = item.Attributes;
                    if (!attributes.HasFlag(FileAttributes.Hidden))
                    {
                        response.Files.Add(await BaseModel.CreateAsync(new FileSystemDirectory(item), path.RootVolume));
                    }
                }
            }

            if (path.RootVolume.MaxUploadSize.HasValue)
            {
                response.Options.UploadMaxSize = $"{path.RootVolume.MaxUploadSizeInKb.Value}K";
            }
            return new ConnectorResult(response);
        }

        public async Task<ConnectorResult> ListAsync(FullPath path, IEnumerable<string> intersect, IEnumerable<string> mimeTypes)
        {
            var response = new ListResponseModel();

            foreach (var item in await path.Directory.GetFilesAsync(mimeTypes))
            {
                if (!item.Attributes.HasFlag(FileAttributes.Hidden))
                {
                    response.List.Add(item.Name);
                }
            }

            foreach (var item in await path.Directory.GetDirectoriesAsync())
            {
                if (!item.Attributes.HasFlag(FileAttributes.Hidden))
                {
                    response.List.Add(item.Name);
                }
            }

            if (intersect.Any())
            {
                response.List.RemoveAll(x => !intersect.Contains(x));
            }

            return new ConnectorResult(response);
        }

        public async Task<ConnectorResult> MakeDirAsync(FullPath path, string name, IEnumerable<string> dirs)
        {
            var response = new AddResponseModel();

            if (!string.IsNullOrEmpty(name))
            {
                var newDir = new FileSystemDirectory(Path.Combine(path.Directory.FullName, name));
                await newDir.CreateAsync();
                response.Added.Add(await BaseModel.CreateAsync(newDir, path.RootVolume));
            }

            foreach (string dir in dirs)
            {
                string dirName = dir.StartsWith("/") ? dir.Substring(1) : dir;
                var newDir = new FileSystemDirectory(Path.Combine(path.Directory.FullName, dirName));
                await newDir.CreateAsync();

                response.Added.Add(await BaseModel.CreateAsync(newDir, path.RootVolume));

                string relativePath = newDir.FullName.Substring(path.RootVolume.RootDirectory.Length);
                response.Hashes.Add($"/{dirName}", path.RootVolume.VolumeId + HttpEncoder.EncodePath(relativePath));
            }

            return new ConnectorResult(response);
        }

        public async Task<ConnectorResult> MakeFileAsync(FullPath path, string name)
        {
            var newFile = new FileSystemFile(Path.Combine(path.Directory.FullName, name));
            await newFile.CreateAsync();

            var response = new AddResponseModel();
            response.Added.Add(await BaseModel.CreateAsync(newFile, path.RootVolume));
            return new ConnectorResult(response);
        }

        public async Task<ConnectorResult> OpenAsync(FullPath path, bool tree, IEnumerable<string> mimeTypes)
        {
            var response = new OpenResponse(await BaseModel.CreateAsync(path.Directory, path.RootVolume), path);
            foreach (var item in await path.Directory.GetFilesAsync(mimeTypes))
            {
                if (!item.Attributes.HasFlag(FileAttributes.Hidden))
                {
                    response.Files.Add(await BaseModel.CreateAsync(item, path.RootVolume));
                }
            }
            foreach (var item in await path.Directory.GetDirectoriesAsync())
            {
                if (!item.Attributes.HasFlag(FileAttributes.Hidden))
                {
                    response.Files.Add(await BaseModel.CreateAsync(item, path.RootVolume));
                }
            }

            // Add parents
            if (tree)
            {
                var parent = path.Directory;

                var rootDirectory = new DirectoryInfo(path.RootVolume.RootDirectory);
                while (parent != null && parent.Name != rootDirectory.Name)
                {
                    // Update parent
                    parent = parent.Parent;

                    // Ensure it's a child of the root
                    if (parent != null && path.RootVolume.RootDirectory.Contains(parent.Name))
                    {
                        response.Files.Insert(0, await BaseModel.CreateAsync(parent, path.RootVolume));
                    }
                }
            }

            return new ConnectorResult(response);
        }

        public async Task<ConnectorResult> ParentsAsync(FullPath path)
        {
            var response = new TreeResponseModel();
            if (path.Directory.FullName == path.RootVolume.RootDirectory)
            {
                response.Tree.Add(await BaseModel.CreateAsync(path.Directory, path.RootVolume));
            }
            else
            {
                var parent = path.Directory;
                foreach (var item in await parent.Parent.GetDirectoriesAsync())
                {
                    response.Tree.Add(await BaseModel.CreateAsync(item, path.RootVolume));
                }
                while (parent.FullName != path.RootVolume.RootDirectory)
                {
                    parent = parent.Parent;
                    response.Tree.Add(await BaseModel.CreateAsync(parent, path.RootVolume));
                }
            }
            return new ConnectorResult(response);
        }

        public async Task<FullPath> ParsePathAsync(string target)
        {
            if (string.IsNullOrEmpty(target))
            {
                return null;
            }

            string volumePrefix = null;
            string pathHash = null;
            for (int i = 0; i < target.Length; i++)
            {
                if (target[i] == '_')
                {
                    pathHash = target.Substring(i + 1);
                    volumePrefix = target.Substring(0, i + 1);
                    break;
                }
            }

            var root = Roots.First(r => r.VolumeId == volumePrefix);
            var rootDirectory = new DirectoryInfo(root.RootDirectory);
            string path = HttpEncoder.DecodePath(pathHash);
            string dirUrl = path != rootDirectory.Name ? path : string.Empty;
            var dir = new FileSystemDirectory(root.RootDirectory + dirUrl);

            if (await dir.ExistsAsync)
            {
                return new FullPath(root, dir, target);
            }
            else
            {
                var file = new FileSystemFile(root.RootDirectory + dirUrl);
                return new FullPath(root, file, target);
            }
        }

        public async Task<ConnectorResult> PasteAsync(FullPath dest, IEnumerable<FullPath> paths, bool isCut, IEnumerable<string> renames, string suffix)
        {
            var response = new ReplaceResponseModel();

            foreach (string rename in renames)
            {
                var fileInfo = new FileInfo(Path.Combine(dest.Directory.FullName, rename));
                string destination = Path.Combine(dest.Directory.FullName, $"{Path.GetFileNameWithoutExtension(rename)}{suffix}{Path.GetExtension(rename)}");
                fileInfo.MoveTo(destination);
                response.Added.Add(await BaseModel.CreateAsync(new FileSystemFile(destination), dest.RootVolume));
            }

            foreach (var src in paths)
            {
                if (src.IsDirectory)
                {
                    var newDir = new FileSystemDirectory(Path.Combine(dest.Directory.FullName, src.Directory.Name));
                    if (await newDir.ExistsAsync)
                    {
                        Directory.Delete(newDir.FullName, true);
                    }

                    if (isCut)
                    {
                        await RemoveThumbsAsync(src);
                        Directory.Move(src.Directory.FullName, newDir.FullName);
                        response.Removed.Add(src.HashedTarget);
                    }
                    else
                    {
                        DirectoryCopy(src.Directory.FullName, newDir.FullName, true);
                    }

                    response.Added.Add(await BaseModel.CreateAsync(newDir, dest.RootVolume));
                }
                else
                {
                    var newFile = new FileSystemFile(Path.Combine(dest.Directory.FullName, src.File.Name));
                    if (await newFile.ExistsAsync)
                    {
                        await newFile.DeleteAsync();
                    }

                    if (isCut)
                    {
                        await RemoveThumbsAsync(src);
                        File.Move(src.File.FullName, newFile.FullName);
                        response.Removed.Add(src.HashedTarget);
                    }
                    else
                    {
                        File.Copy(src.File.FullName, newFile.FullName);
                    }
                    response.Added.Add(await BaseModel.CreateAsync(newFile, dest.RootVolume));
                }
            }
            return new ConnectorResult(response);
        }

        public async Task<ConnectorResult> PutAsync(FullPath path, string content)
        {
            var response = new ChangedResponseModel();
            using (var fileStream = new FileStream(path.File.FullName, FileMode.Create))
            using (var writer = new StreamWriter(fileStream))
            {
                writer.Write(content);
            }
            response.Changed.Add(await BaseModel.CreateAsync(path.File, path.RootVolume));
            return new ConnectorResult(response);
        }

        public async Task<ConnectorResult> PutAsync(FullPath path, byte[] content)
        {
            var response = new ChangedResponseModel();
            using (var fileStream = new FileStream(path.File.FullName, FileMode.Create))
            using (var writer = new BinaryWriter(fileStream))
            {
                writer.Write(content);
            }
            response.Changed.Add(await BaseModel.CreateAsync(path.File, path.RootVolume));
            return new ConnectorResult(response);
        }

        public async Task<ConnectorResult> RemoveAsync(IEnumerable<FullPath> paths)
        {
            var response = new RemoveResponseModel();
            foreach (var path in paths)
            {
                await RemoveThumbsAsync(path);
                try
                {
                    if (path.IsDirectory)
                    {
                        Directory.Delete(path.Directory.FullName, true);
                    }
                    else
                    {
                        File.Delete(path.File.FullName);
                    }
                }
                catch (Exception ex)
                {
                    Trace.TraceError(ex.Message);
                }
                response.Removed.Add(path.HashedTarget);
            }
            return new ConnectorResult(response);
        }

        public async Task<ConnectorResult> RenameAsync(FullPath path, string name)
        {
            var response = new ReplaceResponseModel();
            response.Removed.Add(path.HashedTarget);
            await RemoveThumbsAsync(path);

            if (path.IsDirectory)
            {
                var newPath = new FileSystemDirectory(Path.Combine(path.Directory.Parent.FullName, name));
                string destPath = Path.GetFullPath(newPath.FullName);
                if (!destPath.StartsWith(path.RootVolume.RootDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    throw new NotSupportedException($"Entry '{name}' is outside of the home directory.");
                }
                Directory.Move(path.Directory.FullName, newPath.FullName);
                response.Added.Add(await BaseModel.CreateAsync(newPath, path.RootVolume));
            }
            else
            {
                var newPath = new FileSystemFile(Path.Combine(path.File.DirectoryName, name));
                string destPath = Path.GetFullPath(newPath.FullName);
                if (!destPath.StartsWith(path.RootVolume.RootDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    throw new NotSupportedException($"Entry '{name}' is outside of the home directory.");
                }
                File.Move(path.File.FullName, newPath.FullName);
                response.Added.Add(await BaseModel.CreateAsync(newPath, path.RootVolume));
            }

            return new ConnectorResult(response);
        }

        public async Task<ConnectorResult> ResizeAsync(FullPath path, int width, int height)
        {
            await RemoveThumbsAsync(path);

            // Resize Image
            ImageWithMimeType image;
            using (var stream = new FileStream(path.File.FullName, FileMode.Open))
            {
                image = path.RootVolume.PictureEditor.Resize(stream, width, height);
            }

            using (var fileStream = File.Create(path.File.FullName))
            {
                await image.ImageStream.CopyToAsync(fileStream);
            }

            var output = new ChangedResponseModel();
            output.Changed.Add(await BaseModel.CreateAsync(path.File, path.RootVolume));
            return new ConnectorResult(output);
        }

        public async Task<ConnectorResult> RotateAsync(FullPath path, int degree)
        {
            await RemoveThumbsAsync(path);

            // Rotate Image
            ImageWithMimeType image;
            using (var stream = new FileStream(path.File.FullName, FileMode.Open))
            {
                image = path.RootVolume.PictureEditor.Rotate(stream, degree);
            }

            using (var fileStream = File.Create(path.File.FullName))
            {
                await image.ImageStream.CopyToAsync(fileStream);
            }

            var output = new ChangedResponseModel();
            output.Changed.Add(await BaseModel.CreateAsync(path.File, path.RootVolume));
            return new ConnectorResult(output);
        }

        public async Task<ConnectorResult> SearchAsync(FullPath path, string query, IEnumerable<string> mimeTypes)
        {
            var response = new SearchResponseModel();

            if (!query.Any(Path.GetInvalidFileNameChars().Contains))
            {
                foreach (var item in await path.Directory.GetFilesAsync(mimeTypes, string.Concat("*", query, "*")))
                {
                    if (!item.Attributes.HasFlag(FileAttributes.Hidden) && !item.Directory.Attributes.HasFlag(FileAttributes.Hidden))
                    {
                        response.Files.Add(await BaseModel.CreateAsync(item, path.RootVolume));
                    }
                }

                if (query != ".")
                {
                    foreach (var item in await path.Directory.GetDirectoriesAsync(string.Concat("*", query, "*")))
                    {
                        if (!item.Attributes.HasFlag(FileAttributes.Hidden))
                        {
                            response.Files.Add(await BaseModel.CreateAsync(item, path.RootVolume));
                        }
                    }
                }
            }

            return new ConnectorResult(response);
        }

        public async Task<ConnectorResult> SizeAsync(IEnumerable<FullPath> paths)
        {
            var response = new SizeResponseModel();

            foreach (var path in paths)
            {
                if (path.IsDirectory)
                {
                    response.DirectoryCount++; // API counts the current directory in the total

                    var sizeAndCount = DirectorySizeAndCount(new DirectoryInfo(path.Directory.FullName));

                    response.DirectoryCount += sizeAndCount.DirectoryCount;
                    response.FileCount += sizeAndCount.FileCount;
                    response.Size += sizeAndCount.Size;
                }
                else
                {
                    response.FileCount++;
                    response.Size += await path.File.LengthAsync;
                }
            }

            return new ConnectorResult(response);
        }

        public async Task<ConnectorResult> ThumbsAsync(IEnumerable<FullPath> paths)
        {
            var response = new ThumbsResponseModel();
            foreach (var path in paths)
            {
                response.Images.Add(path.HashedTarget, await path.RootVolume.GenerateThumbHashAsync(path.File));
                //response.Images.Add(target, path.Root.GenerateThumbHash(path.File) + path.File.Extension); // 2018.02.23: Fix
            }
            return new ConnectorResult(response);
        }

        public async Task<ConnectorResult> TreeAsync(FullPath path)
        {
            var response = new TreeResponseModel();
            foreach (var item in await path.Directory.GetDirectoriesAsync())
            {
                if (!item.Attributes.HasFlag(FileAttributes.Hidden))
                {
                    response.Tree.Add(await BaseModel.CreateAsync(item, path.RootVolume));
                }
            }
            return new ConnectorResult(response);
        }

        public async Task<ConnectorResult> UploadAsync(FullPath path, IList<FileContent> files, bool? overwrite, IEnumerable<FullPath> uploadPaths, IEnumerable<string> renames, string suffix)
        {
            var response = new AddResponseModel();

            if (path.RootVolume.MaxUploadSize.HasValue)
            {
                foreach (var file in files)
                {
                    if (file.Length > path.RootVolume.MaxUploadSize.Value)
                    {
                        return new ConnectorResult("errFileMaxSize");
                    }
                }
            }

            foreach (string rename in renames)
            {
                var fileInfo = new FileInfo(Path.Combine(path.Directory.FullName, rename));
                string destination = Path.Combine(path.Directory.FullName, $"{Path.GetFileNameWithoutExtension(rename)}{suffix}{Path.GetExtension(rename)}");
                fileInfo.MoveTo(destination);
                response.Added.Add(await BaseModel.CreateAsync(new FileSystemFile(destination), path.RootVolume));
            }

            foreach (var uploadPath in uploadPaths)
            {
                var directory = uploadPath.Directory;
                while (directory.FullName != path.RootVolume.RootDirectory)
                {
                    response.Added.Add(await BaseModel.CreateAsync(new FileSystemDirectory(directory.FullName), path.RootVolume));
                    directory = directory.Parent;
                }
            }

            int i = 0;
            foreach (var file in files)
            {
                string destination = uploadPaths.Count() > i ? uploadPaths.ElementAt(i).Directory.FullName : path.Directory.FullName;
                var fileInfo = new FileInfo(Path.Combine(destination, Path.GetFileName(file.FileName)));

                if (fileInfo.Exists)
                {
                    if (overwrite ?? path.RootVolume.UploadOverwrite)
                    {
                        fileInfo.Delete();
                        using (var fileStream = File.OpenWrite(fileInfo.FullName))
                        {
                            await file.ContentStream.CopyToAsync(fileStream);
                        }
                        response.Added.Add(await BaseModel.CreateAsync(new FileSystemFile(fileInfo.FullName), path.RootVolume));
                    }
                    else
                    {
                        string newName = CreateNameForCopy(fileInfo, suffix);
                        using (var fileStream = File.OpenWrite(Path.Combine(fileInfo.DirectoryName, newName)))
                        {
                            await file.ContentStream.CopyToAsync(fileStream);
                        }
                        response.Added.Add(await BaseModel.CreateAsync(new FileSystemFile(newName), path.RootVolume));
                    }
                }
                else
                {
                    using (var fileStream = File.OpenWrite(fileInfo.FullName))
                    {
                        await file.ContentStream.CopyToAsync(fileStream);
                    }
                    response.Added.Add(await BaseModel.CreateAsync(new FileSystemFile(fileInfo.FullName), path.RootVolume));
                }

                i++;
            }
            return new ConnectorResult(response);
        }

        #endregion IDriver Members

        private static string CreateNameForCopy(FileInfo file, string suffix)
        {
            string parentPath = file.DirectoryName;
            string name = Path.GetFileNameWithoutExtension(file.Name);
            string extension = file.Extension;

            if (string.IsNullOrEmpty(suffix))
            {
                suffix = "copy";
            }

            string newName = $"{parentPath}{Path.DirectorySeparatorChar}{name} {suffix}{extension}";
            if (!File.Exists(newName))
            {
                return newName;
            }
            else
            {
                for (int i = 1; i < 10; i++)
                {
                    newName = $"{parentPath}{Path.DirectorySeparatorChar}{name} {suffix} {i}{extension}";
                    if (!File.Exists(newName))
                    {
                        return newName;
                    }
                }
            }

            return $"{parentPath}{Path.DirectorySeparatorChar}{name} {suffix} {Guid.NewGuid()}{extension}";
        }

        private void DirectoryCopy(string sourceDirName, string destDirName, bool copySubDirs)
        {
            DirectoryInfo sourceDir = new DirectoryInfo(sourceDirName);
            DirectoryInfo[] dirs = sourceDir.GetDirectories();

            // If the source directory does not exist, throw an exception.
            if (!sourceDir.Exists)
            {
                throw new DirectoryNotFoundException("Source directory does not exist or could not be found: " + sourceDir.FullName);
            }

            // If the destination directory does not exist, create it.
            if (!Directory.Exists(destDirName))
            {
                Directory.CreateDirectory(destDirName);
            }

            // Get the file contents of the directory to copy.
            FileInfo[] files = sourceDir.GetFiles();

            foreach (FileInfo file in files)
            {
                // Create the path to the new copy of the file.
                string temppath = Path.Combine(destDirName, file.Name);

                // Copy the file.
                file.CopyTo(temppath, false);
            }

            // If copySubDirs is true, copy the subdirectories.
            if (copySubDirs)
            {
                foreach (DirectoryInfo subdir in dirs)
                {
                    // Create the subdirectory.
                    string temppath = Path.Combine(destDirName, subdir.Name);

                    // Copy the subdirectories.
                    DirectoryCopy(subdir.FullName, temppath, copySubDirs);
                }
            }
        }

        private SizeResponseModel DirectorySizeAndCount(DirectoryInfo d)
        {
            var response = new SizeResponseModel();

            // Add file sizes.
            foreach (var file in d.GetFiles())
            {
                response.FileCount++;
                response.Size += file.Length;
            }

            // Add subdirectory sizes.
            foreach (var directory in d.GetDirectories())
            {
                response.DirectoryCount++;

                var subdir = DirectorySizeAndCount(directory);
                response.DirectoryCount += subdir.DirectoryCount;
                response.FileCount += subdir.FileCount;
                response.Size += subdir.Size;
            }

            return response;
        }

        private async Task RemoveThumbsAsync(FullPath path)
        {
            if (path.IsDirectory)
            {
                string thumbPath = path.RootVolume.GetExistingThumbPath(path.Directory);
                if (!string.IsNullOrEmpty(thumbPath) && Directory.Exists(thumbPath))
                {
                    Directory.Delete(thumbPath, true);
                }
            }
            else
            {
                string thumbPath = await path.RootVolume.GetExistingThumbPathAsync(path.File);
                if (!string.IsNullOrEmpty(thumbPath) && File.Exists(thumbPath))
                {
                    File.Delete(thumbPath);
                }
            }
        }
    }
}
