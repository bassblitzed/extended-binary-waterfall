using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Transforms;
using Unai.ExtendedBinaryWaterfall.Exporters;
using Unai.ExtendedBinaryWaterfall.Parsers;

namespace Unai.ExtendedBinaryWaterfall;

public class Generator
{
	private Stream _inputFileStream = null;
	private Stream _inputAuxFileStream = null;
	private IParser _parser = null;
	private IExporter _exporter = null;
	private Stopwatch _timer = new();
	
	private List<SubFile> _subfiles = [];

	private Image<Rgba32> _frameContent = null;
	private Image<Rgba32> _viewportFramebuf = null;
	private float[] _inputAudioBuffer = null;
	private float[] _outputAudioBuffer = null;

	// ImageSharp-specific.
	private FontCollection _fontCollection;
	private FontFamily _fontFamily, _emojiFontFamily;
	private Font _font16, _font24, _font32, _font48;
	
	// User-defined input.
	public string InputFilePath { get; set; } = null;
	public string InputAuxiliaryFilePath { get; set; } = null;
	public string Title { get; set; } = null;
	public string Author { get; set; } = null;
	public string InputFileFormatId { get; set; } = null;
	public string ExporterId { get; set; } = null;
	public int InputBytesPerSecond { get; set; } = 220000 * 2 * 2;
	public int InputBytesPerFrame => InputBytesPerSecond / OutputFps;

	// Video parameters.
	public int OutputVideoWidth { get; set; } = 1920;
	public int OutputVideoHeight { get; set; } = 1080;
	public int OutputFps { get; set; } = 60;
	public int WaterfallScaledWidth { get; set; } = 768;
	public int WaterfallScaledHeight { get; set; } = 768;
	public int WaterfallWidth { get; set; } = 256;
	public int WaterfallHeight { get; set; } = 256;
	public int WaterfallFrameLength => WaterfallWidth * WaterfallHeight * 4;

	// Audio parameters.
	public AudioSampleFormat AudioInputSampleFormat { get; set; } = AudioSampleFormat.Signed16LE;
	public int AudioInputChannelCount { get; set; } = 2;
	public int AudioInputSamplesPerFrame => (InputBytesPerFrame / AudioInputSampleFormat.GetByteSize());
	public int AudioInputSampleRate => (InputBytesPerSecond / AudioInputSampleFormat.GetByteSize()) / AudioInputChannelCount;
	public int AudioInputBytesPerFrame => InputBytesPerFrame;

	public AudioSampleFormat AudioOutputSampleFormat => AudioSampleFormat.Float32;
	public int AudioOutputChannelCount => 2;
	public int AudioOutputSampleRate { get; set; } = 48000;
	public int AudioOutputSamplesPerFrame => AudioOutputSampleRate * AudioOutputChannelCount / OutputFps;
	public int AudioOutputBytesPerFrame => AudioOutputSampleFormat.GetByteSize() * AudioOutputSamplesPerFrame;

	public void Generate()
	{
		Logger.Info("Opening files…");
		_inputFileStream = File.OpenRead(InputFilePath);
		if (InputAuxiliaryFilePath != null) _inputAuxFileStream = File.OpenRead(InputAuxiliaryFilePath);

		Logger.Info("Setting up parser…");
		var availableParsers = Utils.GetTypesWithAttribute<ParserAttribute>();

		if (InputFileFormatId != null)
		{
			Logger.Debug($"Requested parser: '{InputFileFormatId}'.");
			foreach (var parser in availableParsers)
			{
				var parserAttr = parser.GetCustomAttribute<ParserAttribute>();
				if (parserAttr.Id != InputFileFormatId)
				{
					continue;
				}
				_parser = (IParser)Activator.CreateInstance(parser);
			}
			if (_parser == null)
			{
				Logger.Warning($"Unknown parser ID: '{InputFileFormatId}'. Skipping subfile listing.");
			}
		}
		else
		{
			Logger.Info("Guessing input format from file extension…");
			var inputFileExt = Path.GetExtension(InputFilePath).ToLower();

			foreach (var parser in availableParsers)
			{
				var parserAttr = parser.GetCustomAttribute<ParserAttribute>();
				if (parserAttr.FileExtensions.Contains(inputFileExt))
				{
					Logger.Debug($"Parser '{parserAttr.Id}' recognizes '{inputFileExt}' as a valid file extension.");
					_parser = (IParser)Activator.CreateInstance(parser);
					break;
				}
			}
			if (_parser == null)
			{
				Logger.Warning($"Unknown input format. Skipping subfile listing.");
			}
		}
		Logger.Debug($"Selected parser: {_parser?.GetType().GetCustomAttribute<ParserAttribute>()?.Name ?? "<null>"}");

		if (_parser != null)
		{
			Logger.Info("Parsing subfiles…");

			_parser.InputStream = _inputFileStream;
			_parser.AuxiliaryInputStream = _inputAuxFileStream;
			_subfiles = _parser.GetSubFiles().ToList();
		}

		using var targetFileReader = new BinaryReader(_inputFileStream);

		_subfiles = [.. _subfiles
			.OrderBy(sf => sf.StartOffset)
			.Select(sf => Utils.ParseSubfile(_inputFileStream, sf))];

		Logger.Debug($"Total number of subfiles: {_subfiles.Count}");

		Logger.Info("Setting up exporter…");
		Logger.Debug($"Requested exporter: '{ExporterId}'.");

		if (ExporterId != null)
		{
			var availableExporters = Utils.GetTypesWithAttribute<ExporterAttribute>();
			foreach (var exporter in availableExporters)
			{
				var exporterAttr = exporter.GetCustomAttribute<ExporterAttribute>();
				if (exporterAttr.Id != ExporterId)
				{
					continue;
				}
				_exporter = (IExporter)Activator.CreateInstance(exporter);
				Logger.Debug($"Exporter {exporterAttr.Name} selected.");
			}
			if (_exporter == null)
			{
				Logger.Fail($"Unknown exporter ID: '{ExporterId}'.");
				return;
			}
		}
		else
		{
			Logger.Debug("No exporter requested. Using SDL…");
			_exporter = new SdlExportHandler();
		}

		_exporter.Generator = this;

		Logger.Info("Preparing audio/video generation…");

		int videoFrameX1 = OutputVideoWidth / (_subfiles.Count > 0 ? 4 : 2) - WaterfallScaledWidth / 2;
		int videoFrameX2 = videoFrameX1 + WaterfallScaledWidth;
		int videoFrameY1 = OutputVideoHeight / 2 - WaterfallScaledHeight / 2;
		int videoFrameY2 = OutputVideoHeight / 2 + WaterfallScaledHeight / 2;

		Logger.Debug($"Speed: {InputBytesPerFrame} b/f ({InputBytesPerSecond} b/s)");
		Logger.Debug($"Waterfall duration will be {TimeSpan.FromSeconds(_inputFileStream.Length / (InputBytesPerSecond))}.");
		Logger.Debug($"Audio input:  {AudioInputBytesPerFrame}bpf {AudioInputSamplesPerFrame}spf → {AudioInputSampleRate}Hz {AudioInputChannelCount}ch {8 * AudioInputSampleFormat.GetByteSize()}-bit");
		Logger.Debug($"Audio output: {AudioOutputBytesPerFrame}bpf {AudioOutputSamplesPerFrame}spf → {AudioOutputSampleRate}Hz {AudioOutputChannelCount}ch {8 * AudioOutputSampleFormat.GetByteSize()}-bit");

		_frameContent = new(OutputVideoWidth, OutputVideoHeight);
		_inputAudioBuffer = new float[AudioInputSamplesPerFrame];
		_outputAudioBuffer = new float[AudioOutputSamplesPerFrame];

		_fontCollection = new();
		_fontCollection.AddSystemFonts();
		_fontFamily = _fontCollection.Get("Consolas");
		_font48 = _fontFamily.CreateFont(48f, FontStyle.Regular);
		_font32 = _fontFamily.CreateFont(32f, FontStyle.Regular);
		_font24 = _fontFamily.CreateFont(24f, FontStyle.Regular);
		_font16 = _fontFamily.CreateFont(16f, FontStyle.Regular);
		_emojiFontFamily = _fontCollection.Get("Consolas");

		_timer.Start();

		// 1. Intro

		GenerateIntro();

		// 2. Main Video

		GenerateMainVideo(targetFileReader, videoFrameX1, videoFrameY1);
	}

	private void GenerateIntro()
	{
		Logger.Info("Generating introduction…");

		var totalFrames = (0.5 * OutputFps); // 60FPS = 300

		for (long frameNumber = 0; frameNumber < totalFrames; frameNumber++)
		{
			_frameContent.Mutate(ctx => ctx.Clear(new Rgba32(16, 16, 16, 255)));

			_frameContent.Mutate(av => av
				.DrawText(new RichTextOptions(_font48)
				{
					Origin = new Vector2(OutputVideoWidth / 2, OutputVideoHeight / 2),
					HorizontalAlignment = HorizontalAlignment.Center,
					TextAlignment = TextAlignment.Center,
				}, "DISCLAIMER\n\nThis video contains\nhigh speed flashing lights\nand loud noises", Color.White)
				.DrawText(new RichTextOptions(_font24)
				{
					Origin = new Vector2(OutputVideoWidth / 2, OutputVideoHeight - 128),
					HorizontalAlignment = HorizontalAlignment.Center,
				}, $"Starting in {(totalFrames - frameNumber) / (float)OutputFps:N1} seconds…", Color.White)
				.DrawProgressBar(frameNumber / (float)totalFrames, (int)(OutputVideoWidth * 0.3), (int)(OutputVideoWidth * 0.7), OutputVideoHeight - 64));

			_exporter.PushNewFrame(_frameContent, _outputAudioBuffer, _timer.Elapsed.TotalSeconds);
			_timer.Restart();
		}
	}

	private void GenerateMainVideo(BinaryReader targetFileReader, int videoFrameX1, int videoFrameY1)
	{
		Logger.Info("Generating binary waterfall…");

		string avSettingsString = $"{AudioInputSampleRate} Hz, PCM {(AudioInputSampleFormat.IsSigned() ? "signed" : "unsigned")} {8 * AudioInputSampleFormat.GetByteSize()}-bit, {(AudioInputChannelCount == 2 ? "stereo" : "mono")}\nRGBA (32bpp), {WaterfallWidth} px/line";
		string readSpeedString = $"{InputBytesPerSecond / 1024} KiB/s";

		float subfileWindowIndex = 0f;
		long currentOffset = 0;
		int playHeadRelPos = 0;

		while (currentOffset < _inputFileStream.Length)
		{
			// Get video buffer.

			playHeadRelPos = 0;
			var frameStartByteOffset = currentOffset.Align(WaterfallWidth * 4) - (WaterfallFrameLength / 2);
			if (frameStartByteOffset < 0)
			{
				playHeadRelPos = (int)-(frameStartByteOffset / (WaterfallWidth * 4));
				frameStartByteOffset = 0;
			}
			else if (frameStartByteOffset + WaterfallFrameLength >= _inputFileStream.Length)
			{
				playHeadRelPos = (int)((_inputFileStream.Length - (frameStartByteOffset + WaterfallFrameLength)) / (WaterfallWidth * 4));
				frameStartByteOffset = _inputFileStream.Length - WaterfallFrameLength;
			}
			var frameEndByteOffset = frameStartByteOffset + WaterfallFrameLength;

			_inputFileStream.Position = frameStartByteOffset;
			var currentVideoBuffer = targetFileReader.ReadBytes(WaterfallFrameLength);

			// Get audio buffer.

			var audioFrameStartByteOffset = currentOffset.Align(AudioInputSampleFormat.GetByteSize()) - (InputBytesPerFrame / 2);
			if (audioFrameStartByteOffset < 0)
			{
				audioFrameStartByteOffset = 0;
			}
			else if (audioFrameStartByteOffset + InputBytesPerFrame >= _inputFileStream.Length)
			{
				audioFrameStartByteOffset = _inputFileStream.Length - InputBytesPerFrame;
			}
			var audioFrameEndByteOffset = audioFrameStartByteOffset + InputBytesPerFrame;

			_inputFileStream.Position = audioFrameStartByteOffset;
			var currentAudioBuffer = targetFileReader.ReadBytes(InputBytesPerFrame);

			// Get video data.

			_viewportFramebuf = Image.LoadPixelData<Rgba32>(currentVideoBuffer, WaterfallWidth, WaterfallHeight);
			_viewportFramebuf.ProcessPixelRows(pa =>
			{
				for (int y = 0; y < pa.Height; y++)
				{
					var row = pa.GetRowSpan(y);
					for (int x = 0; x < row.Length; x++)
					{
						row[x].A = 255;
					}
				}
			});
			_viewportFramebuf.Mutate(ctx => ctx.Flip(FlipMode.Vertical).Resize(WaterfallScaledWidth, WaterfallScaledHeight, new NearestNeighborResampler()));

			// Get audio data.

			switch (AudioInputSampleFormat)
			{
				case AudioSampleFormat.Unsigned8:
					_inputAudioBuffer = currentAudioBuffer.Select(x => (x / 256f) - 1f).ToArray();
					break;

				case AudioSampleFormat.Unsigned16LE:
					for (int i = 0; i < _inputAudioBuffer.Length; i++)
					{
						var srcIdx = i * 2;
						var srcSample = BinaryPrimitives.ReadUInt16LittleEndian(currentAudioBuffer.AsSpan(srcIdx, 2));
						// if (i == 0) Console.Error.WriteLine($"{srcIdx}/{currentAudioBuffer.Length} → {i}/{_inputAudioBuffer.Length} ({srcSample:X4})");
						_inputAudioBuffer[i] = (srcSample / 32768f) - 1f;
					}
					break;

				case AudioSampleFormat.Signed16LE:
					for (int i = 0; i < _inputAudioBuffer.Length; i++)
					{
						var srcIdx = i * 2;
						var srcSample = BinaryPrimitives.ReadInt16LittleEndian(currentAudioBuffer.AsSpan(srcIdx, 2));
						// if (i % 16 == 0) Console.Error.WriteLine($"{srcIdx}/{currentAudioBuffer.Length} → {i}/{_inputAudioBuffer.Length} ({srcSample})");
						_inputAudioBuffer[i] = srcSample / 32768f;
					}
					break;

				default:
					throw new InvalidOperationException("Audio sample format not implemented yet.");
			}

			_outputAudioBuffer = _inputAudioBuffer
				.ToPlanar(2)
				.Select(chData => chData.ToList().LinearResample(AudioOutputSamplesPerFrame / 2).ToList())
				.ToArray()
				.ToPacked()
				.ToArray();

			// Compute registers.

			var subfilesInFrame = _subfiles
				.Select((sf, i) => new { key = i, value = sf })
				.Where(kvp => kvp.value.Intersects(currentOffset - (InputBytesPerFrame / 2), currentOffset + (InputBytesPerFrame / 2)))
				.ToList();
			var mainSubfile = subfilesInFrame.LastOrDefault();

			if (mainSubfile != null)
			{
				subfileWindowIndex = .2f * subfileWindowIndex + .8f * mainSubfile.key;
			}

			// 1. Clear frame

			_frameContent.Mutate(ctx => ctx.Clear(new Rgba32(16, 16, 16, 255)));

			// 2. Draw subfile listing

			int subfileX1 = OutputVideoWidth / 2;
			int subfileX2 = OutputVideoWidth - 32;

			int firstSubfileIndex = (int)(subfileWindowIndex - 7);
			int lastSubfileIndex = (int)Math.Ceiling(subfileWindowIndex + 7);

			float subfileH = 48;
			float subfileY = (OutputVideoHeight / 2) - (subfileWindowIndex - firstSubfileIndex) * subfileH;

			for (int sfi = firstSubfileIndex; sfi <= lastSubfileIndex; sfi++)
			{
				int i = sfi - (mainSubfile?.key ?? 0);

				if (sfi < 0 || sfi >= _subfiles.Count)
				{
					subfileY += subfileH;
					continue;
				}

				var subfile = _subfiles[sfi];

				bool isMainSubfile = sfi == (mainSubfile?.key ?? -1);

				_frameContent.Mutate(ictx => ictx
					.DrawText(new RichTextOptions(_font32)
					{
						Origin = new Vector2(subfileX1, subfileY),
						VerticalAlignment = VerticalAlignment.Center,
						FallbackFontFamilies = [_emojiFontFamily],
					}, $"{(isMainSubfile ? "▶" : " ")} {Utils.GetFileTypeEmoji(subfile)} {Utils.TruncateString(subfile.FileName, 40)}", Color.White)
					.DrawText(new RichTextOptions(_font32)
					{
						Origin = new Vector2(subfileX2, subfileY),
						HorizontalAlignment = HorizontalAlignment.Right,
						VerticalAlignment = VerticalAlignment.Center,
					}, Utils.ToByteSizeString(subfile.Length), Color.DimGray)
				);

				if (isMainSubfile)
				{
					float percentOfSubfile = (currentOffset - subfile.StartOffset) / (float)subfile.Length;

					_frameContent.Mutate(ictx => ictx
						.DrawText(new(_font16)
						{
							Origin = new PointF(subfileX1 + 48, subfileY + 20),
							HorizontalAlignment = HorizontalAlignment.Center,
							VerticalAlignment = VerticalAlignment.Center,
						}, $"{(int)Math.Clamp(percentOfSubfile * 100, 0, 100)} %", Color.White)
						.DrawProgressBar(percentOfSubfile, subfileX1 + 80, subfileX2, subfileY + 20)
					);
				}

				subfileY += subfileH;
			}

			// 3. Draw binary waterfall viewport

			_frameContent.Mutate(ctx => ctx
				.DrawImage(_viewportFramebuf, new Point(videoFrameX1, videoFrameY1), 1f)
				.DrawText(new RichTextOptions(_font32)
				{
					Origin = new Vector2(32, (OutputVideoHeight / 2) + (playHeadRelPos * (WaterfallScaledHeight / WaterfallHeight))),
					VerticalAlignment = VerticalAlignment.Center,
				}, "▶", Color.White)
			);

			// 4. Draw top-bottom gradients

			float shadowY1 = (OutputVideoHeight / 2) - subfileH * 8.5f;
			float shadowY2 = (OutputVideoHeight / 2) + subfileH * 6.5f;

			_frameContent.Mutate(ctx => ctx
				.Fill(
					new LinearGradientBrush(
						new PointF(0, shadowY1),
						new PointF(0, shadowY1 + subfileH * 2),
						GradientRepetitionMode.None,
						new(0.5f, Color.FromRgba(16, 16, 16, 255)),
						new(1, Color.FromRgba(16, 16, 16, 0))
					),
					new RectangleF(0, shadowY1, OutputVideoWidth, subfileH * 2))
				.Fill(
					new LinearGradientBrush(
						new PointF(0, shadowY2),
						new PointF(0, shadowY2 + subfileH * 2),
						GradientRepetitionMode.None,
						new(0, Color.FromRgba(16, 16, 16, 0)),
						new(0.5f, Color.FromRgba(16, 16, 16, 255))
					),
					new RectangleF(0, shadowY2, OutputVideoWidth, subfileH * 2))
			);

			_frameContent.Mutate(ctx => ctx
				.DrawText(new RichTextOptions(_font24)
				{
					Origin = new Vector2(subfileX1 + 40, 160),
					VerticalAlignment = VerticalAlignment.Center,
				}, Utils.TruncateString(mainSubfile?.value?.FileDirectory ?? string.Empty, 72), Color.DimGray)
			);

			// 5. Draw Status and General Info

			_frameContent.Mutate(ctx =>
			{
				ctx
				.DrawText(new RichTextOptions(_font24)
				{
					Origin = new Vector2(32, 32),
				}, "A/V SETTINGS", Color.DimGray)
				.DrawText(new(_font32)
				{
					Origin = new Vector2(32, 32 + 24),
				}, avSettingsString, Color.White)
				.DrawText(new RichTextOptions(_font24)
				{
					Origin = new Vector2(OutputVideoWidth - 32, 32),
					HorizontalAlignment = HorizontalAlignment.Right,
				}, "ABS. OFFSET", Color.DimGray)
				.DrawText(new(_font32)
				{
					Origin = new Vector2(OutputVideoWidth - 32, 32 + 24),
					HorizontalAlignment = HorizontalAlignment.Right,
					TextAlignment = TextAlignment.End,
				}, $"{currentOffset / 1048576f:N2} MiB\n0x{currentOffset:X8}", Color.White)
				.DrawText(new RichTextOptions(_font24)
				{
					Origin = new Vector2(OutputVideoWidth - 256, 32),
					HorizontalAlignment = HorizontalAlignment.Right,
				}, "BITRATE", Color.DimGray)
				.DrawText(new(_font32)
				{
					Origin = new Vector2(OutputVideoWidth - 256, 32 + 24),
					HorizontalAlignment = HorizontalAlignment.Right,
				}, readSpeedString, Color.White);

				if (Author != null)
				{
					ctx.DrawText(new(_font32)
					{
						Origin = new Vector2(OutputVideoWidth / 2, 32 + 24),
						VerticalAlignment = VerticalAlignment.Center,
						HorizontalAlignment = HorizontalAlignment.Center,
					}, Author, Color.White);
				}

				if (Title != null)
				{
					ctx.DrawText(new RichTextOptions(_font24)
					{
						Origin = new Vector2(32, OutputVideoHeight - 64 - (Title.Contains('\n') ? 32 : 0)),
						VerticalAlignment = VerticalAlignment.Bottom,
					}, "TARGET", Color.DimGray)
					.DrawText(new(_font32)
					{
						Origin = new Vector2(32, OutputVideoHeight - 32),
						VerticalAlignment = VerticalAlignment.Bottom,
					}, Title, Color.White);
				}

				if (mainSubfile?.value?.Icon != null)
				{
					ctx.DrawImage(mainSubfile.value.Icon, new Point(OutputVideoWidth / 2, OutputVideoHeight - 128 - 32), 1f);
				}

				if (mainSubfile?.value?.Description != null)
				{
					ctx.DrawText(new(_font32)
					{
						Origin = new Vector2(OutputVideoWidth / 2 + 128 + 32, OutputVideoHeight - 32),
						VerticalAlignment = VerticalAlignment.Bottom,
					}, mainSubfile.value.Description, Color.White);
				}
			});

			_exporter.PushNewFrame(_frameContent, _outputAudioBuffer, _timer.Elapsed.TotalSeconds);
			_timer.Restart();

			currentOffset += InputBytesPerFrame;
		}

		_exporter.Finish();
	}
}