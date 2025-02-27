using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;

namespace elFinder.AspNet.Drivers
{
    /// <summary>
    /// Represents a Base elFinder Driver
    /// </summary>
    public abstract class BaseDriver
    {
        public ICollection<RootVolume> Roots { get; protected set; }

        public string VolumePrefix { get; protected set; }

        /// <summary>
        /// Adds an object to the end of the roots.
        /// </summary>
        /// <param name="item"></param>
        public void AddRoot(RootVolume item)
        {
            if (item.IsStartingVolume && Roots.Any(r => r.IsStartingVolume))
            {
                throw new NotSupportedException("Only one volume can be marked as the starting volume");
            }
            Roots.Add(item);
            item.VolumeId = $"{VolumePrefix}{Roots.Count}_";
        }

        protected virtual async Task AddDirectoryToArchiveAsync(ZipArchive zipFile, IDirectory directoryInfo, string root)
        {
            string entryName = $"{root}{directoryInfo.Name}/";

            zipFile.CreateEntry(entryName);
            var dirs = await directoryInfo.GetDirectoriesAsync();

            foreach (var dir in dirs)
            {
                await AddDirectoryToArchiveAsync(zipFile, dir, entryName);
            }

            var files = await directoryInfo.GetFilesAsync(null);
            foreach (var file in files)
            {
                zipFile.CreateEntryFromFile(file.FullName, entryName + file.Name);
            }
        }

        /*protected Task<JsonResult> Json(object data)
        {
            return Task.FromResult(new JsonResult(data) { ContentType = "text/html" });
        }*/
    }
}
