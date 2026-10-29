using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading.Tasks;
using Brutal.ImGuiApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Core;
using HarmonyLib;
using KSA;
using KSA.Rendering;
using KSATelemetryOverlay;

namespace BrowserServerModule;

/// <summary>
/// High-performance selective game capture hook for KSA.
/// Intercepts KSA's render pipeline before ImGui compositing to capture the pristine 3D scene
/// from Program._offscreenTarget and stream it to the OBS browser source without any in-game HUD.
/// Offloads downscaling, bilinear filtering, and Linear->sRGB conversion to the GPU via vkCmdBlitImage.
/// </summary>
public static class GameCaptureHook
{
    private const string LogPrefix = "[GameCapture] ";
    private const int QueueFamilyIgnored = -1;

    private static Harmony? _harmony;
    private static bool _installed;
    private static FieldInfo? _offscreenField;

    private static readonly Stopwatch _fpsTimer = Stopwatch.StartNew();
    private static double _lastCaptureTime;

    // Triple-buffered staging ring in Host-Visible RAM to eliminate horizontal tearing & GPU stalls
    private const int StagingBufferCount = 3;
    private static readonly BufferEx[] _stagingBuffers = new BufferEx[StagingBufferCount];
    private static ulong _stagingBufferSize;
    private static int _writeIndex = 0;
    private static int _framesRecorded = 0;
    private static byte[]? _cpuStagingBuffer;

    // GPU intermediate downscale image (Device Local VRAM: ~2 MB for 540p)
    private static ImageEx _downscaleImage;
    private static int _downscaleWidth;
    private static int _downscaleHeight;
    private static VkFormat _downscaleFormat;

    private static volatile bool _isEncoding = false;
    private static bool _loggedCaptureInfo = false;

    private static void DisposeStagingBuffers()
    {
        if (_stagingBufferSize == 0 && _downscaleImage.VkImage.IsNull()) return;

        for (int i = 0; i < _stagingBuffers.Length; i++)
        {
            if (!_stagingBuffers[i].IsNull())
            {
                _stagingBuffers[i].Dispose();
                _stagingBuffers[i] = default;
            }
        }

        if (!_downscaleImage.VkImage.IsNull())
        {
            _downscaleImage.Dispose();
            _downscaleImage = default;
        }

        _stagingBufferSize = 0;
        _downscaleWidth = 0;
        _downscaleHeight = 0;
        _cpuStagingBuffer = null;
        _framesRecorded = 0;
        _writeIndex = 0;
    }

    public static bool Installed => _installed;
    public static string? ErrorMessage { get; private set; }

    public static void Install()
    {
        if (_installed) return;

        try
        {
            _offscreenField = typeof(Program).GetField("_offscreenTarget", BindingFlags.Instance | BindingFlags.NonPublic);
            if (_offscreenField is null)
            {
                ErrorMessage = "Could not find Program._offscreenTarget field";
                Console.WriteLine(LogPrefix + ErrorMessage);
                return;
            }

            _harmony = new Harmony("com.elevatorctln.browserservermodule.capture");

            // 1. Patch RenderTarget.CreateColorImage to include VK_IMAGE_USAGE_TRANSFER_SRC_BIT
            MethodInfo? createColorImage = typeof(RenderTarget).GetMethod(
                "CreateColorImage", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (createColorImage != null)
            {
                MethodInfo cciTranspiler = typeof(GameCaptureHook).GetMethod(
                    nameof(CreateColorImageTranspile), BindingFlags.Static | BindingFlags.NonPublic)!;
                _harmony.Patch(createColorImage, transpiler: new HarmonyMethod(cciTranspiler));
            }

            // 2. Patch Program.RenderGame before ImGui renderpass
            MethodInfo? targetMethod = typeof(Program).GetMethod(
                "RenderGame", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (targetMethod is null)
            {
                ErrorMessage = "Could not find Program.RenderGame method";
                Console.WriteLine(LogPrefix + ErrorMessage);
                return;
            }

            MethodInfo transpiler = typeof(GameCaptureHook).GetMethod(
                nameof(RenderGameTranspile), BindingFlags.Static | BindingFlags.NonPublic)!;
            _harmony.Patch(targetMethod, transpiler: new HarmonyMethod(transpiler));

            _installed = true;
            Console.WriteLine(LogPrefix + "installed low-VRAM hardware capture hook on Program.RenderGame");

            // If renderer is already built, schedule rebuild so _offscreenTarget gets TRANSFER_SRC_BIT
            try
            {
                Program.ScheduleRendererRebuild();
            }
            catch { }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            Console.WriteLine(LogPrefix + $"failed to install: {ex.Message}");
        }
    }

    public static void Uninstall()
    {
        if (!_installed) return;

        try
        {
            _harmony?.UnpatchAll("com.elevatorctln.browserservermodule.capture");
            DisposeStagingBuffers();
        }
        catch { }

        _installed = false;
        Console.WriteLine(LogPrefix + "uninstalled capture hook");
    }

    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> CreateColorImageTranspile(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var ins in instructions)
        {
            if (ins.opcode == OpCodes.Ldc_I4_S && Convert.ToInt32(ins.operand) == 28)
            {
                // 28 = ColorAttachment | Storage | Sampled
                // 29 = TransferSrc | ColorAttachment | Storage | Sampled
                yield return new CodeInstruction(OpCodes.Ldc_I4_S, (sbyte)29);
            }
            else
            {
                yield return ins;
            }
        }
    }

    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> RenderGameTranspile(IEnumerable<CodeInstruction> instructions)
    {
        var matcher = new CodeMatcher(instructions);

        matcher.MatchStartForward(
            CodeMatch.Calls(() => default(ImGuiBackendVulkanImpl)!.RenderDrawData(default)));
        matcher.ThrowIfInvalid("could not find ImGuiBackendVulkanImpl.RenderDrawData call in Program.RenderGame");

        matcher.MatchStartBackwards(
            CodeMatch.Calls(() => default(CommandBuffer).BeginRenderPass(default, default)));
        matcher.ThrowIfInvalid("could not find CommandBuffer.BeginRenderPass call in Program.RenderGame");

        matcher.RemoveInstruction();
        matcher.InsertAndAdvance(CodeInstruction.Call(() => ImGuiPreRenderHook(default, default, default)));

        return matcher.Instructions();
    }

    private static unsafe void ImGuiPreRenderHook(
        CommandBuffer commandBuffer, in VkRenderPassBeginInfo beginInfo, VkSubpassContents contents)
    {
        try
        {
            OverlayConfig? config = TelemetryOverlayApi.CurrentConfig;
            if (config is { StreamGameVideo: true } && TelemetryServer.HasVideoClients)
            {
                TryCaptureOffscreen(commandBuffer, config);
            }
            else
            {
                // Release staging buffers immediately when not streaming to guarantee 0 extra VRAM/RAM
                DisposeStagingBuffers();
            }
        }
        catch (Exception ex)
        {
            #if DEBUG
            Console.WriteLine(LogPrefix + $"capture error: {ex.Message}");
            #endif
        }

        // Always proceed with the original ImGui renderpass
        commandBuffer.BeginRenderPass(in beginInfo, contents);
    }

    private static unsafe void TryCaptureOffscreen(CommandBuffer commandBuffer, OverlayConfig config)
    {
        double now = _fpsTimer.Elapsed.TotalSeconds;
        double minInterval = 1.0 / Math.Clamp(config.VideoStreamFps, 5, 60);
        if (now - _lastCaptureTime < minInterval)
        {
            return;
        }

        if (_offscreenField?.GetValue(Program.Instance) is not RenderTarget target)
        {
            return;
        }

        RenderImage? colorImage = target.ColorImage;
        if (colorImage is null) return;

        int srcW = target.Extent.Width;
        int srcH = target.Extent.Height;
        if (srcW <= 0 || srcH <= 0) return;

        int maxHeight = Math.Clamp(config.VideoStreamMaxHeight, 240, 2160);
        int dstW;
        int dstH;
        if (maxHeight > 0 && srcH > maxHeight)
        {
            float scale = (float)maxHeight / srcH;
            dstW = (int)(srcW * scale) & ~1;
            dstH = maxHeight & ~1;
        }
        else
        {
            dstW = srcW & ~1;
            dstH = srcH & ~1;
        }

        if (dstW <= 0 || dstH <= 0) return;

        ulong bufferSize = (ulong)(dstW * dstH * 4);
        Renderer renderer = Program.GetRenderer();

        // 1. Allocate GPU downscale target image and 3 staging buffers if needed
        if (_stagingBuffers[0].IsNull() || _stagingBufferSize != bufferSize || _downscaleImage.VkImage.IsNull() || _downscaleWidth != dstW || _downscaleHeight != dstH)
        {
            DisposeStagingBuffers();
            _stagingBufferSize = bufferSize;

            VkFormat targetFormat = VkFormat.R8G8B8A8SRGB;
            try
            {
                ImageEx.CreateInfo imageInfo = new()
                {
                    Name = "GameCapture_Downscale",
                    ImageType = VkImageType._2D,
                    ImageFormat = targetFormat,
                    ImageExtent = new VkExtent3D(dstW, dstH, 1),
                    ImageMipLevels = 1,
                    ImageArrayLayers = 1,
                    ImageSamples = VkSampleCountFlags._1Bit,
                    ImageTiling = VkImageTiling.Optimal,
                    ImageUsage = VkImageUsageFlags.TransferDstBit | VkImageUsageFlags.TransferSrcBit,
                    ImageSharingMode = VkSharingMode.Exclusive,
                    ImageInitialLayout = VkImageLayout.Undefined,
                    AllocPreference = MemoryPreference.PreferGpu,
                };
                _downscaleImage = renderer.Device.CreateImage(imageInfo);
                _downscaleFormat = targetFormat;
            }
            catch
            {
                // Fallback to UNorm if sRGB not supported for transfer/blit
                targetFormat = VkFormat.R8G8B8A8UNorm;
                ImageEx.CreateInfo imageInfo = new()
                {
                    Name = "GameCapture_Downscale",
                    ImageType = VkImageType._2D,
                    ImageFormat = targetFormat,
                    ImageExtent = new VkExtent3D(dstW, dstH, 1),
                    ImageMipLevels = 1,
                    ImageArrayLayers = 1,
                    ImageSamples = VkSampleCountFlags._1Bit,
                    ImageTiling = VkImageTiling.Optimal,
                    ImageUsage = VkImageUsageFlags.TransferDstBit | VkImageUsageFlags.TransferSrcBit,
                    ImageSharingMode = VkSharingMode.Exclusive,
                    ImageInitialLayout = VkImageLayout.Undefined,
                    AllocPreference = MemoryPreference.PreferGpu,
                };
                _downscaleImage = renderer.Device.CreateImage(imageInfo);
                _downscaleFormat = targetFormat;
            }

            _downscaleWidth = dstW;
            _downscaleHeight = dstH;

            BufferEx.CreateInfo bufInfo = new()
            {
                Name = "GameCapture_Staging",
                BufferSize = (Brutal.ByteSize)(long)bufferSize,
                BufferUsage = VkBufferUsageFlags.TransferDstBit,
                AllocPreference = MemoryPreference.PreferCpu,
                AllocRequiredProperties = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit
            };

            for (int i = 0; i < StagingBufferCount; i++)
            {
                _stagingBuffers[i] = renderer.Device.CreateBuffer(bufInfo);
            }

            _cpuStagingBuffer = new byte[bufferSize];
            _writeIndex = 0;
            _framesRecorded = 0;
            _loggedCaptureInfo = false;
        }

        _lastCaptureTime = now;

        // 2. Read from staging buffer completed 2 frames ago (guaranteed finished by GPU)
        if (_framesRecorded >= 2 && !_isEncoding)
        {
            int readyIndex = (_writeIndex + 1) % StagingBufferCount;
            BufferEx readyBuffer = _stagingBuffers[readyIndex];

            if (!readyBuffer.IsNull() && _cpuStagingBuffer != null)
            {
                _isEncoding = true;

                // Ultra-fast sequential memcpy from PCIe Host-Visible RAM into CPU DDR RAM (~0.15 ms for 540p)
                using (MappedMemory mapped = readyBuffer.Map())
                {
                    mapped.AsSpan().CopyTo(_cpuStagingBuffer);
                }

                int quality = config.VideoStreamQuality;
                byte[] cpuBuffer = _cpuStagingBuffer;
                int encW = dstW;
                int encH = dstH;

                Task.Run(() =>
                {
                    try
                    {
                        byte[]? jpeg = null;
                        fixed (byte* ptr = cpuBuffer)
                        {
                            if (ptr != null)
                            {
                                jpeg = StbJpegEncoder.EncodeRgba(ptr, encW, encH, quality);
                            }
                        }

                        if (jpeg != null)
                        {
                            TelemetryServer.BroadcastVideoFrame(jpeg);
                        }
                    }
                    catch (Exception ex)
                    {
                        #if DEBUG
                        Console.WriteLine(LogPrefix + $"encode error: {ex.Message}");
                        #endif
                    }
                    finally
                    {
                        _isEncoding = false;
                    }
                });
            }
        }

        if (!_loggedCaptureInfo)
        {
            _loggedCaptureInfo = true;
            Console.WriteLine(LogPrefix + $"hardware capture: {srcW}x{srcH} ({colorImage.Format}) -> blit {dstW}x{dstH} ({_downscaleFormat}) at JPEG Q{config.VideoStreamQuality}");
        }

        VkImageSubresourceRange srcRange = new()
        {
            AspectMask = VkImageAspectFlags.ColorBit,
            BaseMipLevel = 0,
            LevelCount = 1,
            BaseArrayLayer = 0,
            LayerCount = 1
        };

        VkImageSubresourceRange dstRange = new()
        {
            AspectMask = VkImageAspectFlags.ColorBit,
            BaseMipLevel = 0,
            LevelCount = 1,
            BaseArrayLayer = 0,
            LayerCount = 1
        };

        // 3. Transition colorImage to TransferSrcOptimal waiting for render pass / composite writes
        TransitionColorImage(commandBuffer, colorImage.Image, srcRange, VkImageLayout.ShaderReadOnlyOptimal, VkImageLayout.TransferSrcOptimal);

        // 4. Transition _downscaleImage to TransferDstOptimal
        TransitionImage(commandBuffer, _downscaleImage.VkImage, dstRange, VkImageLayout.Undefined, VkImageLayout.TransferDstOptimal);

        // 5. Hardware Blit (bilinear downscaling & color space conversion)
        VkImageBlit blit = default;
        blit.SrcSubresource = new VkImageSubresourceLayers
        {
            AspectMask = VkImageAspectFlags.ColorBit,
            MipLevel = 0,
            BaseArrayLayer = 0,
            LayerCount = 1
        };
        blit.DstSubresource = new VkImageSubresourceLayers
        {
            AspectMask = VkImageAspectFlags.ColorBit,
            MipLevel = 0,
            BaseArrayLayer = 0,
            LayerCount = 1
        };

        VkImageBlit* pBlit = &blit;
        VkOffset3D* srcOffsets = (VkOffset3D*)&pBlit->SrcOffsets;
        srcOffsets[0] = new VkOffset3D(0, 0, 0);
        srcOffsets[1] = new VkOffset3D(srcW, srcH, 1);

        VkOffset3D* dstOffsets = (VkOffset3D*)&pBlit->DstOffsets;
        dstOffsets[0] = new VkOffset3D(0, 0, 0);
        dstOffsets[1] = new VkOffset3D(dstW, dstH, 1);

        commandBuffer.BlitImage(
            colorImage.Image,
            VkImageLayout.TransferSrcOptimal,
            _downscaleImage.VkImage,
            VkImageLayout.TransferDstOptimal,
            [blit],
            VkFilter.Linear);

        // 6. Transition _downscaleImage to TransferSrcOptimal for buffer copy
        TransitionImage(commandBuffer, _downscaleImage.VkImage, dstRange, VkImageLayout.TransferDstOptimal, VkImageLayout.TransferSrcOptimal);

        // 7. Copy _downscaleImage to current staging buffer
        VkBufferImageCopy region = new()
        {
            BufferOffset = (Brutal.ByteSize64)0L,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = new VkImageSubresourceLayers
            {
                AspectMask = VkImageAspectFlags.ColorBit,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1
            },
            ImageOffset = new VkOffset3D(0, 0, 0),
            ImageExtent = new VkExtent3D(dstW, dstH, 1)
        };

        commandBuffer.CopyImageToBuffer(
            _downscaleImage.VkImage,
            VkImageLayout.TransferSrcOptimal,
            _stagingBuffers[_writeIndex].VkBuffer,
            [region]);

        // 8. Host memory visibility barrier for the staging buffer to prevent cache tearing
        VkBufferMemoryBarrier2 bufferBarrier = new()
        {
            SrcStageMask = VkPipelineStageFlags2.TransferBit,
            SrcAccessMask = VkAccessFlags2.TransferWriteBit,
            DstStageMask = VkPipelineStageFlags2.HostBit,
            DstAccessMask = VkAccessFlags2.HostReadBit,
            SrcQueueFamilyIndex = QueueFamilyIgnored,
            DstQueueFamilyIndex = QueueFamilyIgnored,
            Buffer = _stagingBuffers[_writeIndex].VkBuffer,
            Offset = new Brutal.ByteSize64(0UL),
            Size = new Brutal.ByteSize64((ulong)(dstW * dstH * 4))
        };

        commandBuffer.PipelineBarrier2(
            ReadOnlySpan<VkMemoryBarrier2>.Empty,
            [bufferBarrier],
            ReadOnlySpan<VkImageMemoryBarrier2>.Empty);

        // 9. Transition colorImage back to ShaderReadOnlyOptimal for subsequent passes
        TransitionColorImage(commandBuffer, colorImage.Image, srcRange, VkImageLayout.TransferSrcOptimal, VkImageLayout.ShaderReadOnlyOptimal);

        // 10. Advance write index in the 3-buffer ring
        _writeIndex = (_writeIndex + 1) % StagingBufferCount;
        _framesRecorded++;
    }

    private static void TransitionColorImage(
        CommandBuffer commandBuffer,
        VkImage image,
        VkImageSubresourceRange range,
        VkImageLayout oldLayout,
        VkImageLayout newLayout)
    {
        if (oldLayout == newLayout) return;

        VkPipelineStageFlags2 srcStage;
        VkAccessFlags2 srcAccess;
        VkPipelineStageFlags2 dstStage;
        VkAccessFlags2 dstAccess;

        if (oldLayout == VkImageLayout.ShaderReadOnlyOptimal && newLayout == VkImageLayout.TransferSrcOptimal)
        {
            // Wait for all prior graphics/color/shader writes to flush to eliminate tearing
            srcStage = VkPipelineStageFlags2.ColorAttachmentOutputBit | VkPipelineStageFlags2.FragmentShaderBit | VkPipelineStageFlags2.ComputeShaderBit;
            srcAccess = VkAccessFlags2.ColorAttachmentWriteBit | VkAccessFlags2.ShaderSampledReadBit | VkAccessFlags2.ShaderStorageWriteBit;
            dstStage = VkPipelineStageFlags2.BlitBit | VkPipelineStageFlags2.TransferBit;
            dstAccess = VkAccessFlags2.TransferReadBit;
        }
        else if (oldLayout == VkImageLayout.TransferSrcOptimal && newLayout == VkImageLayout.ShaderReadOnlyOptimal)
        {
            srcStage = VkPipelineStageFlags2.BlitBit | VkPipelineStageFlags2.TransferBit;
            srcAccess = VkAccessFlags2.TransferReadBit;
            dstStage = VkPipelineStageFlags2.FragmentShaderBit;
            dstAccess = VkAccessFlags2.ShaderSampledReadBit;
        }
        else
        {
            srcStage = VkPipelineStageFlags2.AllCommandsBit;
            srcAccess = VkAccessFlags2.MemoryReadBit | VkAccessFlags2.MemoryWriteBit;
            dstStage = VkPipelineStageFlags2.AllCommandsBit;
            dstAccess = VkAccessFlags2.MemoryReadBit | VkAccessFlags2.MemoryWriteBit;
        }

        VkImageMemoryBarrier2 barrier = new()
        {
            SrcStageMask = srcStage,
            SrcAccessMask = srcAccess,
            DstStageMask = dstStage,
            DstAccessMask = dstAccess,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = QueueFamilyIgnored,
            DstQueueFamilyIndex = QueueFamilyIgnored,
            Image = image,
            SubresourceRange = range,
        };

        commandBuffer.PipelineBarrier2(
            ReadOnlySpan<VkMemoryBarrier2>.Empty,
            ReadOnlySpan<VkBufferMemoryBarrier2>.Empty,
            [barrier]);
    }

    private static void TransitionImage(
        CommandBuffer commandBuffer,
        VkImage image,
        VkImageSubresourceRange range,
        VkImageLayout oldLayout,
        VkImageLayout newLayout)
    {
        if (oldLayout == newLayout) return;

        VkPipelineStageFlags2 srcStage;
        VkAccessFlags2 srcAccess;
        VkPipelineStageFlags2 dstStage;
        VkAccessFlags2 dstAccess;

        if (oldLayout == VkImageLayout.Undefined && newLayout == VkImageLayout.TransferDstOptimal)
        {
            srcStage = VkPipelineStageFlags2.TopOfPipeBit;
            srcAccess = VkAccessFlags2.None;
            dstStage = VkPipelineStageFlags2.BlitBit | VkPipelineStageFlags2.TransferBit;
            dstAccess = VkAccessFlags2.TransferWriteBit;
        }
        else if (oldLayout == VkImageLayout.TransferDstOptimal && newLayout == VkImageLayout.TransferSrcOptimal)
        {
            srcStage = VkPipelineStageFlags2.BlitBit | VkPipelineStageFlags2.TransferBit;
            srcAccess = VkAccessFlags2.TransferWriteBit;
            dstStage = VkPipelineStageFlags2.TransferBit;
            dstAccess = VkAccessFlags2.TransferReadBit;
        }
        else
        {
            srcStage = VkPipelineStageFlags2.AllCommandsBit;
            srcAccess = VkAccessFlags2.MemoryReadBit | VkAccessFlags2.MemoryWriteBit;
            dstStage = VkPipelineStageFlags2.AllCommandsBit;
            dstAccess = VkAccessFlags2.MemoryReadBit | VkAccessFlags2.MemoryWriteBit;
        }

        VkImageMemoryBarrier2 barrier = new()
        {
            SrcStageMask = srcStage,
            SrcAccessMask = srcAccess,
            DstStageMask = dstStage,
            DstAccessMask = dstAccess,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = QueueFamilyIgnored,
            DstQueueFamilyIndex = QueueFamilyIgnored,
            Image = image,
            SubresourceRange = range,
        };

        commandBuffer.PipelineBarrier2(
            ReadOnlySpan<VkMemoryBarrier2>.Empty,
            ReadOnlySpan<VkBufferMemoryBarrier2>.Empty,
            [barrier]);
    }
}
