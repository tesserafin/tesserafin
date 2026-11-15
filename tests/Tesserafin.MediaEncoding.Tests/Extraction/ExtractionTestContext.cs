using System;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller.IO;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.MediaEncoding.Encoder;
using Tesserafin.Model.IO;

namespace Tesserafin.MediaEncoding.Tests.Extraction;

internal sealed class ExtractionTestContext : IDisposable
{
    public ExtractionTestContext()
    {
        FileSystem.Setup(fs => fs.GetFileInfo(It.IsAny<string>())).Returns((string path) =>
        {
            var file = new FileInfo(path);
            return new FileSystemMetadata { FullName = path, Exists = file.Exists, Length = file.Exists ? file.Length : 0, LastWriteTimeUtc = file.LastWriteTimeUtc };
        });
        FileSystem.Setup(fs => fs.DeleteFile(It.IsAny<string>())).Callback((string path) => File.Delete(path));
        Paths.Setup(paths => paths.GetAttachmentFolderPath(It.IsAny<string>())).Returns((string id) => Path.Combine(Root, "attachments", id));
        Paths.Setup(paths => paths.GetAttachmentPath(It.IsAny<string>(), It.IsAny<string>())).Returns((string id, string name) => Path.Combine(Root, "attachments", id, name));
        Paths.Setup(paths => paths.GetSubtitlePath(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>())).Returns((string id, int index, string extension) => Path.Combine(Root, "subtitles", id, index + extension));
        Encoder.Setup(encoder => encoder.EncoderPath).Returns("ffmpeg");
        Encoder.Setup(encoder => encoder.GetInputArgument(It.IsAny<string>(), It.IsAny<Tesserafin.Model.Dto.MediaSourceInfo>())).Returns((string path, Tesserafin.Model.Dto.MediaSourceInfo _) => $"\"{path}\"");
    }

    public string Root { get; } = Directory.CreateTempSubdirectory("tesserafin-extraction-").FullName;

    public Mock<IFileSystem> FileSystem { get; } = new();

    public Mock<IPathManager> Paths { get; } = new();

    public Mock<IMediaEncoder> Encoder { get; } = new();

    public FfmpegProcessRunner Runner { get; } = new(Microsoft.Extensions.Logging.Abstractions.NullLogger<Tesserafin.MediaEncoding.Encoder.FfmpegProcessRunner>.Instance);

    public FfmpegFileExecutor Executor => new(Runner, FileSystem.Object, NullLogger.Instance);

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
