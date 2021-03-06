﻿using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Security.AccessControl;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SMBLibrary;
using SMBLibrary.Client;

namespace SmbAbstraction
{
    public class SMBFile : FileWrapper, IFile
    {
        private readonly ISMBClientFactory _smbClientFactory;
        private readonly ISMBCredentialProvider _credentialProvider;
        private readonly IFileSystem _fileSystem;
        private readonly uint _maxBufferSize;
        private SMBFileInfoFactory _fileInfoFactory => _fileSystem.FileInfo as SMBFileInfoFactory;

        public SMBTransportType transport { get; set; }

        public SMBFile(ISMBClientFactory smbclientFactory, ISMBCredentialProvider credentialProvider,
            IFileSystem fileSystem, uint maxBufferSize = 65536) : base(new FileSystem())
        {
            _smbClientFactory = smbclientFactory;
            _credentialProvider = credentialProvider;
            _fileSystem = fileSystem;
            _maxBufferSize = maxBufferSize;
            transport = SMBTransportType.DirectTCPTransport;
        }

        public override void AppendAllLines(string path, IEnumerable<string> contents)
        {
            if (!path.IsSharePath())
            {
                base.AppendAllLines(path, contents);
                return;
            }

            using (Stream s = OpenWrite(path))
            {
                s.Seek(0, SeekOrigin.End);
                using (StreamWriter sw = new StreamWriter(s))
                {
                    sw.Write(contents);
                }
            }
        }

        public override void AppendAllLines(string path, IEnumerable<string> contents, Encoding encoding)
        {
            if (!path.IsSharePath())
            {
                base.AppendAllLines(path, contents, encoding);
                return;
            }

            using (Stream s = OpenWrite(path))
            {
                s.Seek(0, SeekOrigin.End);
                using (StreamWriter sw = new StreamWriter(s, encoding))
                {
                    sw.Write(contents);
                }
            }
        }

        public override Task AppendAllLinesAsync(string path, IEnumerable<string> contents, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!path.IsSharePath())
            {
                return base.AppendAllLinesAsync(path, contents, cancellationToken);
            }

            return new Task(() => AppendAllLines(path, contents), cancellationToken);
        }

        public override Task AppendAllLinesAsync(string path, IEnumerable<string> contents, Encoding encoding, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!path.IsSharePath())
            {
                return base.AppendAllLinesAsync(path, contents, encoding, cancellationToken);
            }

            return new Task(() => AppendAllLines(path, contents, encoding), cancellationToken);
        }

        public override void AppendAllText(string path, string contents)
        {
            if (!path.IsSharePath())
            {
                base.AppendAllText(path, contents);
                return;
            }

            using (Stream s = OpenWrite(path))
            {
                s.Seek(0, SeekOrigin.End);
                using (StreamWriter sw = new StreamWriter(s))
                {
                    sw.Write(contents);
                }
            }
        }

        public override void AppendAllText(string path, string contents, Encoding encoding)
        {
            if (!path.IsSharePath())
            {
                base.AppendAllText(path, contents, encoding);
                return;
            }

            using (Stream s = OpenWrite(path))
            {
                s.Seek(0, SeekOrigin.End);
                using (StreamWriter sw = new StreamWriter(s, encoding))
                {
                    sw.Write(contents);
                }
            }
        }

        public override Task AppendAllTextAsync(string path, string contents, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!path.IsSharePath())
            {
                return base.AppendAllTextAsync(path, contents, cancellationToken);
            }

            return new Task(() => AppendAllText(path, contents), cancellationToken);
        }

        public override Task AppendAllTextAsync(string path, string contents, Encoding encoding, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!path.IsSharePath())
            {
                return base.AppendAllTextAsync(path, contents, encoding, cancellationToken);
            }

            return new Task(() => AppendAllText(path, contents, encoding), cancellationToken);
        }

        public override StreamWriter AppendText(string path)
        {
            if (!path.IsSharePath())
            {
                return base.AppendText(path);
            }

            Stream s = OpenWrite(path);
            s.Seek(0, SeekOrigin.End);
            return new StreamWriter(s);
        }

        public override void Copy(string sourceFileName, string destFileName)
        {
            using (Stream sourceStream = OpenRead(sourceFileName))
            {
                using (Stream destStream = OpenWrite(destFileName))
                {
                    sourceStream.CopyTo(destStream, Convert.ToInt32(_maxBufferSize));
                }
            }
        }

        public override void Copy(string sourceFileName, string destFileName, bool overwrite)
        {
            if (overwrite && Exists(destFileName))
            {
                Delete(destFileName);
            }

            Copy(sourceFileName, destFileName);
        }

        public override Stream Create(string path)
        {
            if (!path.IsSharePath())
            {
                return base.Create(path);
            }

            return Open(path, FileMode.Create, FileAccess.ReadWrite);
        }

        public override Stream Create(string path, int bufferSize)
        {
            if (!path.IsSharePath())
            {
                return base.Create(path, bufferSize);
            }

            return new BufferedStream(Open(path, FileMode.Create, FileAccess.ReadWrite), bufferSize);
        }

        public override Stream Create(string path, int bufferSize, FileOptions options)
        {
            if (!path.IsSharePath())
            {
                return base.Create(path, bufferSize, options);
            }

            return new BufferedStream(Open(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite, options, null), bufferSize);
        }

        public override StreamWriter CreateText(string path)
        {
            if (!path.IsSharePath())
            {
                return base.CreateText(path);
            }

            return new StreamWriter(OpenWrite(path));
        }

        public override void Delete(string path)
        {
            if (!path.IsSharePath())
            {
                base.Delete(path);
                return;
            }

            if (!path.TryResolveHostnameFromPath(out var ipAddress))
            {
                throw new ArgumentException($"Unable to resolve \"{path.Hostname()}\"");
            }

            NTStatus status = NTStatus.STATUS_SUCCESS;

            var credential = _credentialProvider.GetSMBCredential(path);

            using (var connection = SMBConnection.CreateSMBConnection(_smbClientFactory, ipAddress, transport, credential, _maxBufferSize))
            {
                var shareName = path.ShareName();
                var relativePath = path.RelativeSharePath();
                var directoryPath = _fileSystem.Path.GetDirectoryName(relativePath);

                ISMBFileStore fileStore = connection.SMBClient.TreeConnect(shareName, out status);

                status.HandleStatus();

                int attempts = 0;
                int allowedRetrys = 3;
                object handle;

                do
                {
                    attempts++;

                    status = fileStore.CreateFile(out handle, out FileStatus fileStatus, relativePath, AccessMask.DELETE, 0, ShareAccess.Read,
                        CreateDisposition.FILE_OPEN, CreateOptions.FILE_DELETE_ON_CLOSE, null);
                }
                while (status == NTStatus.STATUS_PENDING && attempts < allowedRetrys);

                status.HandleStatus();

                // There should be a seperate option to delete, but it doesn't seem to exsist in the library we are using, so this should work for now. Really hacky though.
                fileStore.CloseFile(handle);
            }
        }

        public override bool Exists(string path)
        {
            if (!path.IsSharePath())
            {
                return base.Exists(path);
            }

            try
            {
                if (!path.TryResolveHostnameFromPath(out var ipAddress))
                {
                    throw new ArgumentException($"Unable to resolve \"{path.Hostname()}\"");
                }

                NTStatus status = NTStatus.STATUS_SUCCESS;

                var credential = _credentialProvider.GetSMBCredential(path);

                using (var connection = SMBConnection.CreateSMBConnection(_smbClientFactory, ipAddress, transport, credential, _maxBufferSize))
                {
                    var shareName = path.ShareName();
                    var directoryPath = _fileSystem.Path.GetDirectoryName(path);
                    var fileName = _fileSystem.Path.GetFileName(path);

                    ISMBFileStore fileStore = connection.SMBClient.TreeConnect(shareName, out status);

                    status.HandleStatus();

                    status = fileStore.CreateFile(out object handle, out FileStatus fileStatus, directoryPath, AccessMask.GENERIC_READ, 0, ShareAccess.Read,
                        CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE, null);

                    status.HandleStatus();

                    fileStore.QueryDirectory(out List<QueryDirectoryFileInformation> queryDirectoryFileInformation, handle, string.IsNullOrEmpty(fileName) ? "*" : fileName, FileInformationClass.FileDirectoryInformation);

                    foreach (var file in queryDirectoryFileInformation)
                    {
                        if (file.FileInformationClass == FileInformationClass.FileDirectoryInformation)
                        {
                            FileDirectoryInformation fileDirectoryInformation = (FileDirectoryInformation)file;
                            if (fileDirectoryInformation.FileName == fileName)
                            {
                                fileStore.CloseFile(handle);
                                return true;
                            }
                        }
                    }

                    fileStore.CloseFile(handle);
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        public override FileSecurity GetAccessControl(string path)
        {
            if (!path.IsSharePath())
            {
                return base.GetAccessControl(path);
            }

            throw new NotSupportedException();
        }

        public override FileSecurity GetAccessControl(string path, AccessControlSections includeSections)
        {
            if (!path.IsSharePath())
            {
                return base.GetAccessControl(path, includeSections);
            }

            throw new NotSupportedException();
        }

        public override System.IO.FileAttributes GetAttributes(string path)
        {
            if (!path.IsSharePath())
            {
                return base.GetAttributes(path);
            }

            var fileInfo = _fileInfoFactory.FromFileName(path);

            return fileInfo.Attributes;
        }

        public override DateTime GetCreationTime(string path)
        {
            if (!path.IsSharePath())
            {
                return base.GetCreationTime(path);
            }

            var fileInfo = _fileInfoFactory.FromFileName(path);

            return fileInfo.CreationTime;
        }

        public override DateTime GetCreationTimeUtc(string path)
        {
            if (!path.IsSharePath())
            {
                return base.GetCreationTimeUtc(path);
            }

            var fileInfo = _fileInfoFactory.FromFileName(path);

            return fileInfo.CreationTimeUtc;
        }

        public override DateTime GetLastAccessTime(string path)
        {
            if (!path.IsSharePath())
            {
                return base.GetLastAccessTime(path);
            }

            var fileInfo = _fileInfoFactory.FromFileName(path);

            return fileInfo.LastAccessTime;
        }

        public override DateTime GetLastAccessTimeUtc(string path)
        {
            if (!path.IsSharePath())
            {
                return base.GetLastAccessTimeUtc(path);
            }

            var fileInfo = _fileInfoFactory.FromFileName(path);

            return fileInfo.LastAccessTimeUtc;
        }

        public override DateTime GetLastWriteTime(string path)
        {
            if (!path.IsSharePath())
            {
                return base.GetLastWriteTime(path);
            }

            var fileInfo = _fileInfoFactory.FromFileName(path);

            return fileInfo.LastAccessTimeUtc;
        }

        public override DateTime GetLastWriteTimeUtc(string path)
        {
            if (!path.IsSharePath())
            {
                return base.GetLastWriteTimeUtc(path);
            }

            var fileInfo = _fileInfoFactory.FromFileName(path);

            return fileInfo.LastAccessTimeUtc;
        }

        public override void Move(string sourceFileName, string destFileName)
        {
            Move(sourceFileName, destFileName, null, null);
        }

        internal void Move(string sourceFileName, string destFileName, ISMBCredential sourceCredential, ISMBCredential destinationCredential)
        {
            using (Stream sourceStream = OpenRead(sourceFileName, sourceCredential))
            {
                using (Stream destStream = OpenWrite(destFileName, destinationCredential))
                {
                    sourceStream.CopyTo(destStream, Convert.ToInt32(_maxBufferSize));
                }
            }
        }

        public override Stream Open(string path, FileMode mode)
        {
            return Open(path, mode, null);
        }

        private Stream Open(string path, FileMode mode, ISMBCredential credential)
        {
            if (!path.IsSharePath())
            {
                return base.Open(path, mode);
            }

            return Open(path, mode, FileAccess.ReadWrite, credential);
        }

        public override Stream Open(string path, FileMode mode, FileAccess access)
        {
            return Open(path, mode, access, null);
        }

        private Stream Open(string path, FileMode mode, FileAccess access, ISMBCredential credential)
        {
            if (!path.IsSharePath())
            {
                return base.Open(path, mode, access);
            }

            return Open(path, mode, access, FileShare.Read, credential);
        }

        public override Stream Open(string path, FileMode mode, FileAccess access, FileShare share)
        {
            return Open(path, mode, access, share, null);
        }

        private Stream Open(string path, FileMode mode, FileAccess access, FileShare share, ISMBCredential credential)
        {
            if (!path.IsSharePath())
            {
                return base.Open(path, mode, access, share);
            }

            return Open(path, mode, access, share, FileOptions.None, credential);
        }

        internal Stream Open(string path, FileMode mode, FileAccess access, FileShare share, FileOptions fileOptions, ISMBCredential credential)
        {
            if (!path.TryResolveHostnameFromPath(out var ipAddress))
            {
                throw new ArgumentException($"Unable to resolve \"{path.Hostname()}\"");
            }

            NTStatus status = NTStatus.STATUS_SUCCESS;

            AccessMask accessMask = AccessMask.MAXIMUM_ALLOWED;
            ShareAccess shareAccess = ShareAccess.None;
            CreateDisposition disposition = CreateDisposition.FILE_OPEN;
            CreateOptions createOptions;

            switch (fileOptions)
            {
                case FileOptions.DeleteOnClose:
                    createOptions = CreateOptions.FILE_DELETE_ON_CLOSE;
                    break;
                case FileOptions.RandomAccess:
                    createOptions = CreateOptions.FILE_RANDOM_ACCESS;
                    break;
                case FileOptions.SequentialScan:
                    createOptions = CreateOptions.FILE_SEQUENTIAL_ONLY;
                    break;
                case FileOptions.WriteThrough:
                    createOptions = CreateOptions.FILE_WRITE_THROUGH;
                    break;
                case FileOptions.None:
                case FileOptions.Encrypted:     // These two are not suported unless I am missing something 
                case FileOptions.Asynchronous:  //
                default:
                    createOptions = CreateOptions.FILE_NON_DIRECTORY_FILE;
                    break;
            }

            switch (access)
            {
                case FileAccess.Read:
                    accessMask = AccessMask.GENERIC_READ;
                    shareAccess = ShareAccess.Read;
                    break;
                case FileAccess.Write:
                    accessMask = AccessMask.GENERIC_WRITE;
                    shareAccess = ShareAccess.Write;
                    break;
                case FileAccess.ReadWrite:
                    accessMask = AccessMask.GENERIC_ALL;
                    shareAccess = ShareAccess.Write;
                    break;
            }

            if (credential == null)
            {
                credential = _credentialProvider.GetSMBCredential(path);
            }

            if (credential == null)
            {
                throw new Exception($"Unable to find credential for path: {path}");
            }

            var connection = SMBConnection.CreateSMBConnection(_smbClientFactory, ipAddress, transport, credential, _maxBufferSize);

            var shareName = path.ShareName();
            var relativePath = path.RelativeSharePath();

            ISMBFileStore fileStore = connection.SMBClient.TreeConnect(shareName, out status);

            status.HandleStatus();

            switch (mode)
            {
                case FileMode.Create:
                    disposition = CreateDisposition.FILE_CREATE;
                    break;
                case FileMode.CreateNew:
                    disposition = CreateDisposition.FILE_OVERWRITE;
                    break;
                case FileMode.Open:
                    disposition = CreateDisposition.FILE_OPEN;
                    break;
                case FileMode.OpenOrCreate:
                    disposition = Exists(path) ? CreateDisposition.FILE_OPEN : CreateDisposition.FILE_CREATE;
                    break;
            }

            int getInfoAttempts = 0;
            int getInfoAllowedRetrys = 5;
            
            object handle;
            FileInformation fileInfo;

            do
            {
                getInfoAttempts++;
                int openAttempts = 0;
                int openAllowedRetrys = 5;

                do
                {
                    openAttempts++;

                    status = fileStore.CreateFile(out handle, out FileStatus fileStatus, relativePath, accessMask, 0, shareAccess,
                    disposition, createOptions, null);
                }
                while (status == NTStatus.STATUS_PENDING && openAttempts < openAllowedRetrys);

                status.HandleStatus();
                status = fileStore.GetFileInformation(out fileInfo, handle, FileInformationClass.FileStandardInformation);
            }
            while (status == NTStatus.STATUS_NETWORK_NAME_DELETED && getInfoAttempts < getInfoAllowedRetrys);
            
            status.HandleStatus();

            var fileStandardInfo = (FileStandardInformation)fileInfo;

            Stream s = new SMBStream(fileStore, handle, connection, fileStandardInfo.EndOfFile);

            if (mode == FileMode.Append)
            {
                s.Seek(0, SeekOrigin.End);
            }
            
            return s;
        }

        public override Stream OpenRead(string path)
        {
            return OpenRead(path, null);
        }

        private Stream OpenRead(string path, ISMBCredential credential)
        {
            if (!path.IsSharePath())
            {
                return base.OpenRead(path);
            }

            return Open(path, FileMode.Open, FileAccess.Read, credential);
        }

        public override StreamReader OpenText(string path)
        {
            if (!path.IsSharePath())
            {
                return base.OpenText(path);
            }

            return new StreamReader(OpenRead(path));
        }

        public override Stream OpenWrite(string path)
        {
            return OpenWrite(path, null);
        }

        private Stream OpenWrite(string path, ISMBCredential credential)
        {
            if (!path.IsSharePath())
            {
                return base.OpenWrite(path);
            }

            return Open(path, FileMode.OpenOrCreate, FileAccess.Write, credential);
        }

        public override byte[] ReadAllBytes(string path)
        {
            if (!path.IsSharePath())
            {
                return base.ReadAllBytes(path);
            }

            using (MemoryStream ms = new MemoryStream())
            {
                using (Stream s = OpenRead(path))
                {
                    s.CopyTo(ms, Convert.ToInt32(_maxBufferSize));
                }
                return ms.ToArray();
            }

        }

        public override Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!path.IsSharePath())
            {
                return base.ReadAllBytesAsync(path, cancellationToken);
            }

            return new Task<byte[]>(() => ReadAllBytes(path), cancellationToken);
        }

        public override string[] ReadAllLines(string path)
        {
            if (!path.IsSharePath())
            {
                return base.ReadAllLines(path);
            }

            return ReadLines(path).ToArray();
        }

        public override string[] ReadAllLines(string path, Encoding encoding)
        {
            if (!path.IsSharePath())
            {
                return base.ReadAllLines(path, encoding);
            }

            return ReadLines(path, encoding).ToArray();
        }

        public override Task<string[]> ReadAllLinesAsync(string path, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!path.IsSharePath())
            {
                return base.ReadAllLinesAsync(path, cancellationToken);
            }

            return new Task<string[]>(() => ReadAllLines(path), cancellationToken);
        }

        public override Task<string[]> ReadAllLinesAsync(string path, Encoding encoding, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!path.IsSharePath())
            {
                return base.ReadAllLinesAsync(path, encoding, cancellationToken);
            }

            return new Task<string[]>(() => ReadAllLines(path, encoding), cancellationToken);
        }

        public override string ReadAllText(string path)
        {
            if (!path.IsSharePath())
            {
                return base.ReadAllText(path);
            }

            using (StreamReader sr = new StreamReader(OpenRead(path)))
            {
                return sr.ReadToEnd();
            }
        }

        public override string ReadAllText(string path, Encoding encoding)
        {
            if (!path.IsSharePath())
            {
                return base.ReadAllText(path, encoding);
            }

            using (StreamReader sr = new StreamReader(OpenRead(path), encoding))
            {
                return sr.ReadToEnd();
            }
        }

        public override Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!path.IsSharePath())
            {
                return base.ReadAllTextAsync(path, cancellationToken);
            }

            return new Task<string>(() => ReadAllText(path), cancellationToken);
        }

        public override Task<string> ReadAllTextAsync(string path, Encoding encoding, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!path.IsSharePath())
            {
                return base.ReadAllTextAsync(path, encoding, cancellationToken);
            }

            return new Task<string>(() => ReadAllText(path, encoding), cancellationToken);
        }

        public override IEnumerable<string> ReadLines(string path)
        {
            if (!path.IsSharePath())
            {
                return base.ReadLines(path);
            }

            List<string> lines = new List<string>();
            using (StreamReader sr = new StreamReader(OpenRead(path)))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    lines.Add(line);
                }
            }

            return lines.ToArray();
        }

        public override IEnumerable<string> ReadLines(string path, Encoding encoding)
        {
            if (!path.IsSharePath())
            {
                return base.ReadLines(path, encoding);
            }

            List<string> lines = new List<string>();
            using (StreamReader sr = new StreamReader(OpenRead(path), encoding))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    lines.Add(line);
                }
            }

            return lines.ToArray();
        }

        public override void SetAccessControl(string path, FileSecurity fileSecurity)
        {
            if (!path.IsSharePath())
            {
                base.SetAccessControl(path, fileSecurity);
                return;
            }
            throw new NotSupportedException();
        }

        public override void SetAttributes(string path, System.IO.FileAttributes fileAttributes)
        {
            if (!path.IsSharePath())
            {
                base.SetAttributes(path, fileAttributes);
                return;
            }
            throw new NotSupportedException();
        }

        public override void SetCreationTime(string path, DateTime creationTime)
        {
            if (!path.IsSharePath())
            {
                base.SetCreationTime(path, creationTime);
                return;
            }

            var fileInfo = _fileInfoFactory.FromFileName(path);
            fileInfo.CreationTime = creationTime.ToUniversalTime();
            _fileInfoFactory.SaveFileInfo((SMBFileInfo)fileInfo);
        }

        public override void SetCreationTimeUtc(string path, DateTime creationTimeUtc)
        {
            if (!path.IsSharePath())
            {
                base.SetCreationTimeUtc(path, creationTimeUtc);
                return;
            }

            var fileInfo = _fileInfoFactory.FromFileName(path);
            fileInfo.CreationTime = creationTimeUtc;
            _fileInfoFactory.SaveFileInfo((SMBFileInfo)fileInfo);
        }

        public override void SetLastAccessTime(string path, DateTime lastAccessTime)
        {
            if (!path.IsSharePath())
            {
                base.SetLastAccessTime(path, lastAccessTime);
                return;
            }

            var fileInfo = _fileInfoFactory.FromFileName(path);
            fileInfo.LastAccessTime = lastAccessTime.ToUniversalTime();
            _fileInfoFactory.SaveFileInfo((SMBFileInfo)fileInfo);
        }

        public override void SetLastAccessTimeUtc(string path, DateTime lastAccessTimeUtc)
        {
            if (!path.IsSharePath())
            {
                base.SetLastAccessTimeUtc(path, lastAccessTimeUtc);
                return;
            }

            var fileInfo = _fileInfoFactory.FromFileName(path);
            fileInfo.LastAccessTime = lastAccessTimeUtc;
            _fileInfoFactory.SaveFileInfo((SMBFileInfo)fileInfo);
        }

        public override void SetLastWriteTime(string path, DateTime lastWriteTime)
        {
            if (!path.IsSharePath())
            {
                base.SetLastWriteTime(path, lastWriteTime);
                return;
            }

            var fileInfo = _fileInfoFactory.FromFileName(path);
            fileInfo.LastWriteTime = lastWriteTime.ToUniversalTime();
            _fileInfoFactory.SaveFileInfo((SMBFileInfo)fileInfo);
        }

        public override void SetLastWriteTimeUtc(string path, DateTime lastWriteTimeUtc)
        {
            if (!path.IsSharePath())
            {
                base.SetLastWriteTimeUtc(path, lastWriteTimeUtc);
                return;
            }

            var fileInfo = _fileInfoFactory.FromFileName(path);
            fileInfo.LastWriteTime = lastWriteTimeUtc;
            _fileInfoFactory.SaveFileInfo((SMBFileInfo)fileInfo);
        }

        public override void WriteAllBytes(string path, byte[] bytes)
        {
            if (!path.IsSharePath())
            {
                base.WriteAllBytes(path, bytes);
                return;
            }

            using (StreamWriter sr = new StreamWriter(OpenWrite(path)))
            {
                sr.Write(bytes);
            }
        }

        public override Task WriteAllBytesAsync(string path, byte[] bytes, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!path.IsSharePath())
            {
                return base.WriteAllBytesAsync(path, bytes, cancellationToken);
            }

            return new Task(() => WriteAllBytes(path, bytes), cancellationToken);
        }

        public override void WriteAllLines(string path, IEnumerable<string> contents)
        {
            if (!path.IsSharePath())
            {
                base.WriteAllLines(path, contents);
                return;
            }

            WriteAllLines(path, contents.ToArray());
        }

        public override void WriteAllLines(string path, IEnumerable<string> contents, Encoding encoding)
        {
            if (!path.IsSharePath())
            {
                base.WriteAllLines(path, contents, encoding);
                return;
            }

            WriteAllLines(path, contents.ToArray(), encoding);
        }

        public override void WriteAllLines(string path, string[] contents)
        {
            if (!path.IsSharePath())
            {
                base.WriteAllLines(path, contents);
                return;
            }

            using (StreamWriter sr = new StreamWriter(OpenWrite(path)))
            {
                sr.Write(contents);
            }
        }

        public override void WriteAllLines(string path, string[] contents, Encoding encoding)
        {
            if (!path.IsSharePath())
            {
                base.WriteAllLines(path, contents, encoding);
                return;
            }

            using (StreamWriter sr = new StreamWriter(OpenWrite(path), encoding))
            {
                sr.Write(contents);
            }
        }

        public override Task WriteAllLinesAsync(string path, IEnumerable<string> contents, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!path.IsSharePath())
            {
                return base.WriteAllLinesAsync(path, contents, cancellationToken);
            }

            return new Task(() => WriteAllLines(path, contents), cancellationToken);
        }

        public override Task WriteAllLinesAsync(string path, IEnumerable<string> contents, Encoding encoding, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!path.IsSharePath())
            {
                return base.WriteAllLinesAsync(path, contents, encoding, cancellationToken);
            }

            return new Task(() => WriteAllLines(path, contents, encoding), cancellationToken);
        }

        public override Task WriteAllLinesAsync(string path, string[] contents, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!path.IsSharePath())
            {
                return base.WriteAllLinesAsync(path, contents, cancellationToken);
            }

            return new Task(() => WriteAllLines(path, contents), cancellationToken);
        }

        public override Task WriteAllLinesAsync(string path, string[] contents, Encoding encoding, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!path.IsSharePath())
            {
                return base.WriteAllLinesAsync(path, contents, encoding, cancellationToken);
            }

            return new Task(() => WriteAllLines(path, contents, encoding), cancellationToken);
        }

        public override void WriteAllText(string path, string contents)
        {
            if (!path.IsSharePath())
            {
                base.WriteAllText(path, contents);
                return;
            }

            using (StreamWriter sw = new StreamWriter(OpenWrite(path)))
            {
                sw.Write(contents);
            }
        }

        public override void WriteAllText(string path, string contents, Encoding encoding)
        {
            if (!path.IsSharePath())
            {
                base.WriteAllText(path, contents, encoding);
                return;
            }

            using (StreamWriter sw = new StreamWriter(OpenWrite(path), encoding))
            {
                sw.Write(contents);
            }
        }

        public override Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!path.IsSharePath())
            {
                return base.WriteAllTextAsync(path, contents, cancellationToken);
            }

            return new Task(() => WriteAllText(path, contents), cancellationToken);
        }

        public override Task WriteAllTextAsync(string path, string contents, Encoding encoding, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!path.IsSharePath())
            {
                return base.WriteAllTextAsync(path, contents, encoding, cancellationToken);
            }

            return new Task(() => WriteAllText(path, contents, encoding), cancellationToken);
        }
    }
}
