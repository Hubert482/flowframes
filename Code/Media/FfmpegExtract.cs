﻿using Flowframes.Data;
using Flowframes.IO;
using Flowframes.Main;
using Flowframes.MiscUtils;
using Flowframes.UI;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using static Flowframes.AvProcess;

namespace Flowframes.Media
{
    partial class FfmpegExtract : FfmpegCommands
    {
        public static async Task ExtractSceneChanges(string inPath, string outDir, Fraction rate, bool inputIsFrames, string format)
        {
            Logger.Log("Extracting scene changes...");
            Directory.CreateDirectory(outDir);

            string inArg = $"-i {inPath.Wrap()}";

            if (inputIsFrames)
            {
                string concatFile = Path.Combine(Paths.GetDataPath(), "png-scndetect-concat-temp.ini");
                GetConcatFile(inPath, concatFile);
                inArg = $"-f concat -safe 0 -i {concatFile.Wrap()}";
            }

            string scnDetect = $"-vf \"select='gt(scene,{Config.GetFloatString("scnDetectValue")})'\"";
            string rateArg = (rate.GetFloat() > 0) ? $"-r {rate}" : "";
            string args = $"-vsync 0 {GetTrimArg(true)} {inArg} {GetImgArgs(format)} {rateArg} {scnDetect} -frame_pts 1 -s 256x144 {GetTrimArg(false)} \"{outDir}/%{Padding.inputFrames}d{format}\"";

            LogMode logMode = await Interpolate.GetCurrentInputFrameCount() > 50 ? LogMode.OnlyLastLine : LogMode.Hidden;
            await RunFfmpeg(args, logMode, inputIsFrames ? "panic" : "warning", TaskType.ExtractFrames, true);

            bool hiddenLog = await Interpolate.GetCurrentInputFrameCount() <= 50;
            int amount = IOUtils.GetAmountOfFiles(outDir, false);
            Logger.Log($"Detected {amount} scene {(amount == 1 ? "change" : "changes")}.".Replace(" 0 ", " no "), false, !hiddenLog);
        }

        static string GetImgArgs(string extension, bool includePixFmt = true, bool alpha = false)
        {
            extension = extension.ToLower().Remove(".").Replace("jpeg", "jpg");
            string pixFmt = "-pix_fmt rgb24";
            string args = "";

            if (extension.Contains("png"))
            {
                pixFmt = alpha ? "rgba" : "rgb24";
                args = $"{pngCompr}";
            }

            if (extension.Contains("jpg"))
            {
                pixFmt = "yuv420p";
                args = $"-q:v 1";
            }

            if (extension.Contains("webp"))
            {
                pixFmt = "yuv420p";
                args = $"-q:v 100";
            }

            if (includePixFmt)
                args += $" -pix_fmt {pixFmt}";

            return args;
        }

        public static async Task VideoToFrames(string inputFile, string framesDir, bool alpha, Fraction rate, bool deDupe, bool delSrc, Size size, string format)
        {
            Logger.Log("Extracting video frames from input video...");
            Logger.Log($"VideoToFrames() - Alpha: {alpha} - Size: {size}", true, false, "ffmpeg");
            string sizeStr = (size.Width > 1 && size.Height > 1) ? $"-s {size.Width}x{size.Height}" : "";
            IOUtils.CreateDir(framesDir);
            string mpStr = deDupe ? ((Config.GetInt("mpdecimateMode") == 0) ? mpDecDef : mpDecAggr) : "";
            string filters = FormatUtils.ConcatStrings(new[] { GetPadFilter(), mpStr });
            string vf = filters.Length > 2 ? $"-vf {filters}" : "";
            string rateArg = (rate.GetFloat() > 0) ? $" -r {rate}" : "";
            string args = $"{GetTrimArg(true)} -i {inputFile.Wrap()} {GetImgArgs(format, true, alpha)} -vsync 0 {rateArg} -frame_pts 1 {vf} {sizeStr} {GetTrimArg(false)} \"{framesDir}/%{Padding.inputFrames}d{format}\"";
            LogMode logMode = await Interpolate.GetCurrentInputFrameCount()  > 50 ? LogMode.OnlyLastLine : LogMode.Hidden;
            await RunFfmpeg(args, logMode, TaskType.ExtractFrames, true);
            int amount = IOUtils.GetAmountOfFiles(framesDir, false, "*" + format);
            Logger.Log($"Extracted {amount} {(amount == 1 ? "frame" : "frames")} from input.", false, true);
            await Task.Delay(1);
            if (delSrc)
                DeleteSource(inputFile);
        }

        public static async Task ImportImagesCheckCompat(string inpath, string outpath, bool alpha, Size size, bool showLog, string format)
        {
            bool compatible = await Task.Run(async () => { return AreImagesCompatible(inpath, Config.GetInt("maxVidHeight")); });

            if (!alpha && compatible)
            {
                if (showLog) Logger.Log($"Copying images from {new DirectoryInfo(inpath).Name}...");
                Directory.CreateDirectory(outpath);

                await Task.Run(async () => {
                    int counter = 0;
                    foreach (FileInfo file in IOUtils.GetFileInfosSorted(inpath))
                    {
                        string newFilename = counter.ToString().PadLeft(Padding.inputFrames, '0') + file.Extension;
                        File.Copy(file.FullName, Path.Combine(outpath, newFilename));
                        counter++;
                    }
                });

            }
            else
            {
                await ImportImages(inpath, outpath, alpha, size, showLog, format);
            }
        }

        static bool AreImagesCompatible (string inpath, int maxHeight)
        {
            NmkdStopwatch sw = new NmkdStopwatch();
            string[] validExtensions = new string[] { ".jpg", ".jpeg", ".png" };
            FileInfo[] files = IOUtils.GetFileInfosSorted(inpath);

            if (files.Length < 1)
            {
                Logger.Log("[AreImagesCompatible] Sequence not compatible: No files found.", true);
                return false;
            }

            bool allSameExtension = files.All(x => x.Extension == files.First().Extension);

            if (!allSameExtension)
            {
                Logger.Log($"Sequence not compatible: Not all files have the same extension.", true);
                return false;
            }

            bool allValidExtension = files.All(x => validExtensions.Contains(x.Extension));

            if (!allValidExtension)
            {
                Logger.Log($"Sequence not compatible: Not all files have a valid extension ({string.Join(", ", validExtensions)}).", true);
                return false;
            }

            Image[] randomSamples = files.OrderBy(arg => Guid.NewGuid()).Take(10).Select(x => IOUtils.GetImage(x.FullName)).ToArray();

            bool allSameSize = randomSamples.All(i => i.Size == randomSamples.First().Size);

            if (!allSameSize)
            {
                Logger.Log($"Sequence not compatible: Not all images have the same dimensions [{sw.GetElapsedStr()}].", true);
                return false;
            }

            int div = GetPadding();
            bool allDivBy2 = randomSamples.All(i => (i.Width % div == 0) && (i.Height % div == 0));

            if (!allDivBy2)
            {
                Logger.Log($"Sequence not compatible: Not all image dimensions are divisible by {div} [{sw.GetElapsedStr()}].", true);
                return false;
            }

            bool allSmallEnough = randomSamples.All(i => (i.Height <= maxHeight));

            if (!allSmallEnough)
            {
                Logger.Log($"Sequence not compatible: Image dimensions above max size [{sw.GetElapsedStr()}].", true);
                return false;
            }

            bool all24Bit = randomSamples.All(i => (i.PixelFormat == System.Drawing.Imaging.PixelFormat.Format24bppRgb));

            if (!all24Bit)
            {
                Logger.Log($"Sequence not compatible: Some images are not 24-bit (8bpp) [{sw.GetElapsedStr()}].", true);
                return false;
            }

            Interpolate.current.framesExt = files.First().Extension;
            Logger.Log($"Sequence compatible! [{sw.GetElapsedStr()}]", true);
            return true;
        }

        public static async Task ImportImages(string inpath, string outpath, bool alpha, Size size, bool showLog, string format)
        {
            if (showLog) Logger.Log($"Importing images from {new DirectoryInfo(inpath).Name}...");
            Logger.Log($"ImportImages() - Alpha: {alpha} - Size: {size}", true, false, "ffmpeg");
            IOUtils.CreateDir(outpath);
            string concatFile = Path.Combine(Paths.GetDataPath(), "png-concat-temp.ini");
            GetConcatFile(inpath, concatFile);

            string inArg = $"-f concat -safe 0 -i {concatFile.Wrap()}";
            string linksDir = Path.Combine(concatFile + Paths.symlinksSuffix);

            if (Config.GetBool("allowSymlinkEncoding", true) && Symlinks.SymlinksAllowed())
            {
                if (await Symlinks.MakeSymlinksForEncode(concatFile, linksDir, Padding.interpFrames))
                    inArg = $"-i \"{linksDir}/%{Padding.interpFrames}d.png\"";
            }

            string sizeStr = (size.Width > 1 && size.Height > 1) ? $"-s {size.Width}x{size.Height}" : "";
            string vf = alpha ? $"-filter_complex \"[0:v]{GetPadFilter()},split[a][b];[a]palettegen=reserve_transparent=on:transparency_color=ffffff[p];[b][p]paletteuse\"" : $"-vf {GetPadFilter()}";
            string args = $"-r 25 {inArg} {GetImgArgs(format, true, alpha)} {sizeStr} -vsync 0 -start_number 0 {vf} \"{outpath}/%{Padding.inputFrames}d{format}\"";
            LogMode logMode = IOUtils.GetAmountOfFiles(inpath, false) > 50 ? LogMode.OnlyLastLine : LogMode.Hidden;
            await RunFfmpeg(args, logMode, "panic", TaskType.ExtractFrames);
        }

        public static void GetConcatFile(string inputFilesDir, string concatFilePath)
        {
            string concatFileContent = "";
            string[] files = IOUtils.GetFilesSorted(inputFilesDir);

            foreach (string img in files)
                concatFileContent += $"file '{img.Replace(@"\", "/")}'\n";

            File.WriteAllText(concatFilePath, concatFileContent);
        }

        public static string[] GetTrimArgs()
        {
            return new string[] { GetTrimArg(true), GetTrimArg(false) };
        }

        public static string GetTrimArg(bool input)
        {
            if (!QuickSettingsTab.trimEnabled)
                return "";

            int fastSeekThresh = 180;
            bool fastSeek = QuickSettingsTab.trimStartSecs > fastSeekThresh;
            string arg = "";

            if (input)
            {
                if (fastSeek)
                    arg += $"-ss {QuickSettingsTab.trimStartSecs - fastSeekThresh}";
                else
                    return arg;
            }
            else
            {
                if (fastSeek)
                {
                    arg += $"-ss {fastSeekThresh}";

                    long trimTimeSecs = QuickSettingsTab.trimEndSecs - QuickSettingsTab.trimStartSecs;

                    if (QuickSettingsTab.doTrimEnd)
                        arg += $" -to {fastSeekThresh + trimTimeSecs}";
                }
                else
                {
                    arg += $"-ss {QuickSettingsTab.trimStart}";

                    if (QuickSettingsTab.doTrimEnd)
                        arg += $" -to {QuickSettingsTab.trimEnd}";
                }
            }

            return arg;
        }

        public static async Task ImportSingleImage(string inputFile, string outPath, Size size)
        {
            string sizeStr = (size.Width > 1 && size.Height > 1) ? $"-s {size.Width}x{size.Height}" : "";
            bool isPng = (Path.GetExtension(outPath).ToLower() == ".png");
            string comprArg = isPng ? pngCompr : "";
            string pixFmt = "-pix_fmt " + (isPng ? $"rgb24 {comprArg}" : "yuvj420p");
            string args = $"-i {inputFile.Wrap()} {comprArg} {sizeStr} {pixFmt} -vf {GetPadFilter()} {outPath.Wrap()}";
            await RunFfmpeg(args, LogMode.Hidden, TaskType.ExtractFrames);
        }

        public static async Task ExtractSingleFrame(string inputFile, string outputPath, int frameNum)
        {
            bool isPng = (Path.GetExtension(outputPath).ToLower() == ".png");
            string comprArg = isPng ? pngCompr : "";
            string pixFmt = "-pix_fmt " + (isPng ? $"rgb24 {comprArg}" : "yuvj420p");
            string args = $"-i {inputFile.Wrap()} -vf \"select=eq(n\\,{frameNum})\" -vframes 1 {pixFmt} {outputPath.Wrap()}";
            await RunFfmpeg(args, LogMode.Hidden, TaskType.ExtractFrames);
        }

        public static async Task ExtractLastFrame(string inputFile, string outputPath, Size size)
        {
            if (QuickSettingsTab.trimEnabled)
                return;

            if (IOUtils.IsPathDirectory(outputPath))
                outputPath = Path.Combine(outputPath, "last.png");

            bool isPng = (Path.GetExtension(outputPath).ToLower() == ".png");
            string comprArg = isPng ? pngCompr : "";
            string pixFmt = "-pix_fmt " + (isPng ? $"rgb24 {comprArg}" : "yuvj420p");
            string sizeStr = (size.Width > 1 && size.Height > 1) ? $"-s {size.Width}x{size.Height}" : "";
            string trim = QuickSettingsTab.trimEnabled ? $"-ss {QuickSettingsTab.GetTrimEndMinusOne()} -to {QuickSettingsTab.trimEnd}" : "";
            string sseof = string.IsNullOrWhiteSpace(trim) ? "-sseof -1" : "";
            string args = $"{sseof} -i {inputFile.Wrap()} -update 1 {pixFmt} {sizeStr} {trim} {outputPath.Wrap()}";
            await RunFfmpeg(args, LogMode.Hidden, TaskType.ExtractFrames);
        }
    }
}
