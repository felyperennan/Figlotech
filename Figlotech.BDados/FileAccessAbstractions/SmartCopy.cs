﻿using Figlotech.BDados.DataAccessAbstractions;
using Figlotech.BDados.Interfaces;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Figlotech.BDados.FileAcessAbstractions
{
    public class FileData
    {
        public string RelativePath;
        public long Date;
        public long Length;
        public string Hash;
    }
    /// <summary>
    /// This is meant to provide easy in-program "robocopy" utility.
    /// </summary>
    public class SmartCopy
    {
        IFileAccessor local;
        IFileAccessor remote;
        ISmartCopyOptions options;

        /// <summary>
        /// Provides easy in-program robust copy utility
        /// </summary>
        /// <param name="localAccessor">"Local" or "Origin" accessor</param>
        public SmartCopy(IFileAccessor localAccessor, ISmartCopyOptions copyOptions)
        {
            local = localAccessor;
            options = copyOptions;
        }
        
        /// <summary>
        /// Sets the "other" file acessor to work with;
        /// </summary>
        /// <param name="localAccessor">"Remote" or "Destination" accessor</param>
        public void SetRemote(IFileAccessor remoteAccessor) {
            remote = remoteAccessor;
        }

        private static string GetHash(Stream stream) {
            using (var md5 = MD5.Create()) {
                return Convert.ToBase64String(md5.ComputeHash(stream));
            }
        }

        private String GetHash(IFileAccessor fa, String path) {
            string hash = "";
            fa.Read(path, (stream) => {
                hash = GetHash(stream);
            });
            
            return hash;
        }

        /// <summary>
        /// Copies files from local/origin accessor to remote/destination accessor
        /// Uploads data from the local to the remote accessor
        /// </summary>
        public void MirrorUp(string path = "") {
            var totalFilesCount = CountFiles(local, path);
            OnReportTotalFilesCount?.Invoke(totalFilesCount);

            Mirror(local, remote, path);
        }

        /// <summary>
        /// Copies files remote/destination from accessor to local/origin accessor
        /// Downloads data from the remote to the local accessor
        /// </summary>
        public void MirrorDown(string path = "") {
            if(options.UseHashList) {
                MirrorFromList(path);
            } else {
                var totalFilesCount = CountFiles(remote, path);
                OnReportTotalFilesCount?.Invoke(totalFilesCount);
                Mirror(remote, local, path);
            }
        }

        private int CountFiles(IFileAccessor origin, string path) {
            int count = 0;
            origin.ForFilesIn(path, (f) => {
                count++;
            });

            if (options.Recursive) {
                origin.ForDirectoriesIn(path, (dir) => {
                    count += CountFiles(origin, dir);
                });
            }

            return count;
        }
        /// <summary>
        /// The copier triggers this event to notify when it finishes counting files.
        /// </summary>
        public event Action<int> OnReportTotalFilesCount;
        /// <summary>
        /// The copier triggers this event to notify that it finished processing a file.
        /// The arguments are Boolean saying rather the file changed or not and String with the 
        /// relative file path.
        /// </summary>
        public event Action<bool, String> OnReportProcessedFile;

        /// <summary>
        /// <para>
        /// Set to null to use default copy criteria
        /// Default criteria uses SmartCopyOptions to decide rather the file should be copied or not.
        /// </para>
        /// <para>
        /// The copier will run your function to decide if it should or not copy the file, return true and the copier will copy
        /// return false and it will not, the parameter is the relative file path (you can use it with your file accessor
        /// </para>
        /// <para>
        /// With a new revolutionary technology you can define your won copy criteria through a Lambda expression.
        /// </para>
        /// </summary>
        public Func<String, bool> CopyDecisionCriteria { get; set; }

        public Action<String> OnFileStaged { get; set; }

        private List<FileData> HashList = new List<FileData>();

        private bool Changed(IFileAccessor o, IFileAccessor d, String f) {
            if (options.UseHashList) {
                var match = HashList.FirstOrDefault(fd => fd.RelativePath == f);
                var hash = GetHash(o, f);
                if (match != null) {
                    if(hash != match.Hash) {
                        return true;
                    }
                } else {
                    HashList.Add(
                        new FileData {
                            RelativePath = f,
                            Hash = hash,
                            Date = o.GetLastFileWrite(f)?.Ticks??0,
                            Length = o.GetSize(f)
                        });
                }
            }

            bool changed = false;
            var destLen = d.GetSize(f);
            var oriLen = o.GetSize(f);
            if (oriLen != destLen) {
                return true;
            } else
            if (options.UseHash) {
                var originHash = GetHash(o, f);
                var destinationHash = GetHash(d, f);
                if(originHash == destinationHash) {
                    return true;
                }
            } else {
                var originDate = o.GetLastFileWrite(f);
                var destinationDate = d.GetLastFileWrite(f);
                changed =
                    (
                        (originDate > destinationDate) ||
                        (
                            (originDate < destinationDate && !options.IgnoreOlder)
                        )
                    );
                if (changed) return true;
            }

            return false;
        }

        private void MirrorFromList(string path) {
            int numWorkers = options.Multithreaded ? options.NumWorkers : 1;
            var wq = new WorkQueuer("SmartCopy_Operation", numWorkers, false);
            var bufferSize = (int)options.BufferSize / options.NumWorkers;
            if (bufferSize < 0) bufferSize = Int32.MaxValue;
            List<FileData> workingList = HashList.Where(f => f.RelativePath.StartsWith(path)).ToList();
            OnReportTotalFilesCount?.Invoke(workingList.Count);
            foreach (var a in HashList) {
                wq.Enqueue(() => {
                    var hash = "";
                    local.Read(a.RelativePath, (stream) => {
                        hash = GetHash(stream);
                    });
                    var processed = false;
                    if (a.Hash != hash) {
                        processed = true;
                        if (remote.Exists(a.RelativePath)) {
                            remote.Read(a.RelativePath, (downStream) => {
                                local.Delete(a.RelativePath);
                                local.Write(a.RelativePath, (fileStream) => {
                                    downStream.CopyTo(fileStream, bufferSize);
                                });
                            });
                        }
                    }

                    OnReportProcessedFile?.Invoke(processed, a.RelativePath);
                });
            }
            wq.Start();
            wq.Stop();
        }

        private void Mirror(IFileAccessor origin, IFileAccessor destination, string path, WorkQueuer wq = null)
        {
            bool rec = wq == null;
            if(wq == null) {
                int numWorkers = options.Multithreaded ? options.NumWorkers : 1;
                wq = new WorkQueuer("SmartCopy_Operation", numWorkers, false);
            }
            if(options.UseHashList) {
                try {
                    var txt = destination.ReadAllText(".fth-hashlist");
                    HashList = JsonConvert.DeserializeObject<List<FileData>>(txt);
                } catch (Exception) {
                }
            }
            workedFiles = 0;
            origin.ForFilesIn(path, (f) => {
                wq.Enqueue(() => {

                    try {
                        OnFileStaged?.Invoke(f);
                    } catch (Exception) {
                        return;
                    }

                    var changed = CopyDecisionCriteria != null ?
                        CopyDecisionCriteria(f) :
                        Changed(origin, destination, f);

                    if (changed) {
                        processFile(origin, destination, f);
                    }

                    OnReportProcessedFile?.Invoke(changed, f);
                });
            });
            
            if(options.AllowDelete) {
                destination.ForFilesIn(path, (f) => {
                    if (!origin.Exists(f)) {
                        destination.Delete(f);
                    }
                });
            }
            if (options.Recursive) {
                origin.ForDirectoriesIn(path, (dir) => {
                    destination.MkDirs(dir);
                    Mirror(origin, destination, dir, wq);
                });
            }
            if (rec) {
                wq.Start();
            }

            if(options.UseHashList) {
                HashList.RemoveAll((f) =>
                    !origin.Exists(path)
                    );

                if (HashList.Count > 0) {
                    destination.Write($"$index.info", (stream) => {
                        string text = JsonConvert.SerializeObject(HashList);
                        byte[] writev = Encoding.UTF8.GetBytes(text);
                        stream.Write(writev, 0, writev.Length);
                    });
                    origin.Write($"$index.info", (stream) => {
                        string text = JsonConvert.SerializeObject(HashList);
                        byte[] writev = Encoding.UTF8.GetBytes(text);
                        stream.Write(writev, 0, writev.Length);
                    });
                }
            }

            wq.Stop();
        }

        private String ProcessPath(string path) {
            var l1 = path.Split('\\');
            var mid = Path.Combine(l1);
            var l2 = path.Split('/');
            return Path.Combine(l2);
        }

        private void processFile(IFileAccessor origin, IFileAccessor destination, string workingFile) {
            workingFile = ProcessPath(workingFile);

            origin.Read(workingFile, (input) => {
                var bufferSize = (int)options.BufferSize / options.NumWorkers;
                if (bufferSize < 0) bufferSize = Int32.MaxValue;
                destination.Write(workingFile, (output) => {
                    input.CopyTo(output, bufferSize);
                });
            });
        }

    }
}
