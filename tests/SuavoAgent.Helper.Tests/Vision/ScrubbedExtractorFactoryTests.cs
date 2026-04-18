using Microsoft.Extensions.Options;
using Serilog;
using SuavoAgent.Core.Config;
using SuavoAgent.Helper.Vision;
using Xunit;

namespace SuavoAgent.Helper.Tests.Vision;

public class ScrubbedExtractorFactoryTests
{
    private static readonly ILogger Log = new LoggerConfiguration().CreateLogger();

    [Fact]
    public void CreateDefault_ReturnsNullWrappedInScrub()
    {
        var extractor = ScrubbedExtractorFactory.CreateDefault();
        // Returned type is PhiScrubbingExtractor (internal). We can only check
        // behavior via the public interface.
        Assert.Equal("null", extractor.ExtractorId);
        Assert.True(extractor.IsReady);
    }

    [Fact]
    public void Create_TesseractDisabled_ReturnsNull()
    {
        var opts = Options.Create(new AgentOptions
        {
            Vision = new VisionOptions
            {
                Tesseract = new TesseractOptions { Enabled = false },
            },
        });

        var extractor = ScrubbedExtractorFactory.Create(opts, Log);
        Assert.Equal("null", extractor.ExtractorId);
    }

    [Fact]
    public void Create_TesseractEnabledButMissingPath_FallsBackToNull()
    {
        var opts = Options.Create(new AgentOptions
        {
            Vision = new VisionOptions
            {
                Tesseract = new TesseractOptions
                {
                    Enabled = true,
                    TessdataPath = "/does/not/exist",
                    Language = "eng",
                },
            },
        });

        var extractor = ScrubbedExtractorFactory.Create(opts, Log);
        Assert.Equal("null", extractor.ExtractorId);
    }

    [Fact]
    public void Create_TesseractEnabledWithTrainedData_SelectsTesseract()
    {
        // Fake the minimum filesystem state Tesseract needs to be "reachable":
        // a directory containing {lang}.traineddata.
        var dir = Path.Combine(Path.GetTempPath(),
            "suavo-fact-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "eng.traineddata"), new byte[] { 0 });

            var opts = Options.Create(new AgentOptions
            {
                Vision = new VisionOptions
                {
                    Tesseract = new TesseractOptions
                    {
                        Enabled = true,
                        TessdataPath = dir,
                        Language = "eng",
                    },
                },
            });

            var extractor = ScrubbedExtractorFactory.Create(opts, Log);
            Assert.Equal("tesseract-eng", extractor.ExtractorId);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Create_TesseractWrongLanguage_FallsBackToNull()
    {
        var dir = Path.Combine(Path.GetTempPath(),
            "suavo-fact-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "eng.traineddata"), new byte[] { 0 });

            var opts = Options.Create(new AgentOptions
            {
                Vision = new VisionOptions
                {
                    Tesseract = new TesseractOptions
                    {
                        Enabled = true,
                        TessdataPath = dir,
                        Language = "fra", // fra.traineddata doesn't exist → fallback
                    },
                },
            });

            var extractor = ScrubbedExtractorFactory.Create(opts, Log);
            Assert.Equal("null", extractor.ExtractorId);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void TesseractOptions_SaneDefaults()
    {
        var opts = new TesseractOptions();
        Assert.False(opts.Enabled);
        Assert.Equal("eng", opts.Language);
        Assert.Equal(50, opts.MinConfidence);
        Assert.Equal(120, opts.IdleUnloadSeconds);
    }
}
