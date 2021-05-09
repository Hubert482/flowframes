﻿using Flowframes.Media;
using Flowframes.Data;
using Flowframes.IO;
using Flowframes.MiscUtils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Flowframes.UI;

namespace Flowframes.Main
{
    class AutoEncode
    {
        static string interpFramesFolder;
        static string videoChunksFolder;
        public static int chunkSize;    // Encode every n frames
        public static int safetyBufferFrames;      // Ignore latest n frames to avoid using images that haven't been fully encoded yet
        public static string[] interpFramesLines;
        public static List<int> encodedFrameLines = new List<int>();
        public static List<int> unencodedFrameLines = new List<int>();

        public static bool debug;
        public static bool busy;
        public static bool paused;

        public static void UpdateChunkAndBufferSizes ()
        {
            chunkSize = GetChunkSize((IOUtils.GetAmountOfFiles(Interpolate.current.framesFolder, false, "*" + Interpolate.current.framesExt) * Interpolate.current.interpFactor).RoundToInt());

            safetyBufferFrames = 90;

            if (Interpolate.current.ai.aiName.ToUpper().Contains("NCNN"))
                safetyBufferFrames = Config.GetInt("autoEncSafeBufferNcnn", 150);

            if (Interpolate.current.ai.aiName == Networks.rifeCuda.aiName)
                safetyBufferFrames = Config.GetInt("autoEncSafeBufferRifeCuda", 90);

            if (Interpolate.current.ai.aiName == Networks.flavrCuda.aiName)
                safetyBufferFrames = Config.GetInt("autoEncSafeBufferFlavrCuda", 90);
        }

        public static async Task MainLoop(string interpFramesPath)
        {
            debug = Config.GetBool("autoEncDebug", false);

            try
            {
                UpdateChunkAndBufferSizes();

                interpFramesFolder = interpFramesPath;
                videoChunksFolder = Path.Combine(interpFramesPath.GetParentDir(), Paths.chunksDir);
                if (Interpolate.currentlyUsingAutoEnc)
                    Directory.CreateDirectory(videoChunksFolder);

                encodedFrameLines.Clear();
                unencodedFrameLines.Clear();

                Logger.Log($"[AutoEnc] Starting AutoEncode MainLoop - Chunk Size: {chunkSize} Frames - Safety Buffer: {safetyBufferFrames} Frames", true);
                int videoIndex = 1;
                string encFile = Path.Combine(interpFramesPath.GetParentDir(), Paths.GetFrameOrderFilename(Interpolate.current.interpFactor));
                interpFramesLines = IOUtils.ReadLines(encFile).Select(x => x.Split('/').Last().Remove("'").Split('#').First()).ToArray();     // Array with frame filenames

                while (!Interpolate.canceled && GetInterpFramesAmount() < 2)
                    await Task.Delay(2000);

                int lastEncodedFrameNum = 0;

                while (HasWorkToDo())    // Loop while proc is running and not all frames have been encoded
                {
                    if (Interpolate.canceled) return;

                    if (paused)
                    {
                        await Task.Delay(200);
                        continue;
                    }

                    unencodedFrameLines.Clear();

                    bool aiRunning = !AiProcess.lastAiProcess.HasExited;

                    for (int frameLineNum = lastEncodedFrameNum; frameLineNum < interpFramesLines.Length; frameLineNum++)
                    {
                        if (aiRunning && interpFramesLines[frameLineNum].Contains(InterpolationProgress.lastFrame.ToString().PadLeft(Padding.interpFrames, '0')))
                            break;

                        unencodedFrameLines.Add(frameLineNum);
                    }

                    if (unencodedFrameLines.Count > 0 && (unencodedFrameLines.Count >= (chunkSize + safetyBufferFrames) || !aiRunning))     // Encode every n frames, or after process has exited
                    {
                        try
                        {
                            List<int> frameLinesToEncode = aiRunning ? unencodedFrameLines.Take(chunkSize).ToList() : unencodedFrameLines;     // Take all remaining frames if process is done
                            string lastOfChunk = Path.Combine(interpFramesPath, interpFramesLines[frameLinesToEncode.Last()]);

                            if (!File.Exists(lastOfChunk))
                            {
                                if(debug)
                                    Logger.Log($"[AutoEnc] Last frame of chunk doesn't exist; skipping loop iteration ({lastOfChunk})", true);

                                await Task.Delay(500);
                                continue;
                            }

                            busy = true;
                            string outpath = Path.Combine(videoChunksFolder, "chunks", $"{videoIndex.ToString().PadLeft(4, '0')}{FFmpegUtils.GetExt(Interpolate.current.outMode)}");
                            int firstLineNum = frameLinesToEncode.First();
                            int lastLineNum = frameLinesToEncode.Last();
                            Logger.Log($"[AutoEnc] Encoding Chunk #{videoIndex} to '{outpath}' using line {firstLineNum} ({Path.GetFileName(interpFramesLines[firstLineNum])}) through {lastLineNum} ({Path.GetFileName(Path.GetFileName(interpFramesLines[frameLinesToEncode.Last()]))})", true, false, "ffmpeg");

                            await CreateVideo.EncodeChunk(outpath, Interpolate.current.outMode, firstLineNum, frameLinesToEncode.Count);

                            if (Interpolate.canceled) return;

                            if (aiRunning && Config.GetInt("autoEncMode") == 2)
                                Task.Run(() => DeleteOldFramesAsync(interpFramesPath, frameLinesToEncode));

                            if (Interpolate.canceled) return;

                            encodedFrameLines.AddRange(frameLinesToEncode);

                            Logger.Log("Done Encoding Chunk #" + videoIndex, true, false, "ffmpeg");
                            lastEncodedFrameNum = (frameLinesToEncode.Last() + 1);

                            videoIndex++;
                            busy = false;
                        }
                        catch (Exception e)
                        {
                            Logger.Log($"AutoEnc Chunk Encoding Error: {e.Message}. Stack Trace:\n{e.StackTrace}");
                            Interpolate.Cancel("Auto-Encode encountered an error.");
                        }
                    }
                    await Task.Delay(50);
                }

                if (Interpolate.canceled) return;
                await CreateVideo.ChunksToVideos(Interpolate.current.tempFolder, videoChunksFolder, Interpolate.current.outPath);
            }
            catch (Exception e)
            {
                Logger.Log($"AutoEnc Error: {e.Message}. Stack Trace:\n{e.StackTrace}");
                Interpolate.Cancel("Auto-Encode encountered an error.");
            }
        }

        static async Task DeleteOldFramesAsync (string interpFramesPath, List<int> frameLinesToEncode)
        {
            if(debug)
                Logger.Log("[AutoEnc] Starting DeleteOldFramesAsync.", true, false, "ffmpeg");

            Stopwatch sw = new Stopwatch();
            sw.Restart();

            foreach (int frame in frameLinesToEncode)
            {
                if (!FrameIsStillNeeded(interpFramesLines[frame], frame))    // Make sure frames are no longer needed (for dupes) before deleting!
                {
                    string framePath = Path.Combine(interpFramesPath, interpFramesLines[frame]);
                    IOUtils.OverwriteFileWithText(framePath);    // Overwrite to save space without breaking progress counter
                }
            }

            if (debug)
                Logger.Log("[AutoEnc] DeleteOldFramesAsync finished in " + FormatUtils.TimeSw(sw), true, false, "ffmpeg");
        }

        static bool FrameIsStillNeeded (string frameName, int frameIndex)
        {
            if ((frameIndex + 1) < interpFramesLines.Length && interpFramesLines[frameIndex+1].Contains(frameName))
                return true;
            return false;
        }

        public static bool HasWorkToDo ()
        {
            if (Interpolate.canceled || interpFramesFolder == null) return false;

            if(debug)
                Logger.Log($"HasWorkToDo - Process Running: {(AiProcess.lastAiProcess != null && !AiProcess.lastAiProcess.HasExited)} - encodedFrameLines.Count: {encodedFrameLines.Count} - interpFramesLines.Length: {interpFramesLines.Length}", true);
            
            return ((AiProcess.lastAiProcess != null && !AiProcess.lastAiProcess.HasExited) || encodedFrameLines.Count < interpFramesLines.Length);
        }

        static int GetChunkSize(int targetFramesAmount)
        {
            if (targetFramesAmount > 100000) return 4800;
            if (targetFramesAmount > 50000) return 2400;
            if (targetFramesAmount > 20000) return 1200;
            if (targetFramesAmount > 5000) return 600;
            if (targetFramesAmount > 1000) return 300;
            return 150;
        }

        static int GetInterpFramesAmount()
        {
            return IOUtils.GetAmountOfFiles(interpFramesFolder, false, "*" + Interpolate.current.interpExt);
        }
    }
}
