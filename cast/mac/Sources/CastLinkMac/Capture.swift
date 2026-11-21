// Screen and sound capture (ScreenCaptureKit) and H.264 encoding (VideoToolbox, on the GPU's
// encoder where there is one). The encoder hands AVCC frames to PageServer.video; the sound
// goes out as 16-bit stereo PCM, the format the page plays (see WebCast.cs: 0x03 messages).

import CoreMedia
import CoreVideo
import Foundation
import ScreenCaptureKit
import VideoToolbox

final class H264Encoder {
    private var session: VTCompressionSession?
    private let width: Int32, height: Int32
    /// encoded frame: AVCC data, keyframe, pts in microseconds, SPS and PPS (on keyframes)
    var onFrame: (([UInt8], Bool, Int64, [UInt8]?, [UInt8]?) -> Void)?
    var forceKey = true

    init(width: Int, height: Int, fps: Int, bitrate: Int) throws {
        self.width = Int32(width)
        self.height = Int32(height)
        var s: VTCompressionSession?
        // low-latency rate control where the encoder has it; otherwise the normal one
        let lowLatency = [kVTVideoEncoderSpecification_EnableLowLatencyRateControl: kCFBooleanTrue] as CFDictionary
        var st = VTCompressionSessionCreate(allocator: nil, width: self.width, height: self.height, codecType: kCMVideoCodecType_H264,
                                            encoderSpecification: lowLatency, imageBufferAttributes: nil, compressedDataAllocator: nil,
                                            outputCallback: nil, refcon: nil, compressionSessionOut: &s)
        if st != noErr || s == nil {
            st = VTCompressionSessionCreate(allocator: nil, width: self.width, height: self.height, codecType: kCMVideoCodecType_H264,
                                            encoderSpecification: nil, imageBufferAttributes: nil, compressedDataAllocator: nil,
                                            outputCallback: nil, refcon: nil, compressionSessionOut: &s)
        }
        guard st == noErr, let session = s else { throw NSError(domain: "CastLink", code: Int(st), userInfo: [NSLocalizedDescriptionKey: "H.264 encoder: error \(st)"]) }
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_RealTime, value: kCFBooleanTrue)
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_ProfileLevel, value: kVTProfileLevel_H264_High_AutoLevel)
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_AllowFrameReordering, value: kCFBooleanFalse)
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_AverageBitRate, value: NSNumber(value: bitrate))
        // peaks up to 1.5x the average over one second
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_DataRateLimits, value: [NSNumber(value: bitrate * 3 / 2 / 8), NSNumber(value: 1)] as CFArray)
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_ExpectedFrameRate, value: NSNumber(value: fps))
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_MaxKeyFrameInterval, value: NSNumber(value: fps * 10))
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_MaxKeyFrameIntervalDuration, value: NSNumber(value: 10))
        VTCompressionSessionPrepareToEncodeFrames(session)
        self.session = session
    }

    var isHardware: Bool {
        guard let s = session else { return false }
        var v: CFTypeRef?
        let st = withUnsafeMutablePointer(to: &v) {
            VTSessionCopyProperty(s, key: kVTCompressionPropertyKey_UsingHardwareAcceleratedVideoEncoder, allocator: nil, valueOut: $0)
        }
        return st == noErr && (v as? Bool) == true
    }

    func encode(_ pb: CVPixelBuffer, pts: CMTime) {
        guard let s = session else { return }
        var props: CFDictionary?
        if forceKey {
            forceKey = false
            props = [kVTEncodeFrameOptionKey_ForceKeyFrame: kCFBooleanTrue] as CFDictionary
        }
        VTCompressionSessionEncodeFrame(s, imageBuffer: pb, presentationTimeStamp: pts, duration: .invalid,
                                        frameProperties: props, infoFlagsOut: nil) { [weak self] status, _, sb in
            guard status == noErr, let sb = sb, let self = self else { return }
            self.output(sb)
        }
    }

    private func output(_ sb: CMSampleBuffer) {
        guard let block = CMSampleBufferGetDataBuffer(sb) else { return }
        var key = true
        if let att = CMSampleBufferGetSampleAttachmentsArray(sb, createIfNecessary: false) as? [[CFString: Any]],
           let first = att.first, let notSync = first[kCMSampleAttachmentKey_NotSync] as? Bool {
            key = !notSync
        }
        let len = CMBlockBufferGetDataLength(block)
        var data = [UInt8](repeating: 0, count: len)
        guard CMBlockBufferCopyDataBytes(block, atOffset: 0, dataLength: len, destination: &data) == noErr else { return }
        var sps: [UInt8]?, pps: [UInt8]?
        if key, let fd = CMSampleBufferGetFormatDescription(sb) {
            sps = parameterSet(fd, 0)
            pps = parameterSet(fd, 1)
        }
        let pts = CMSampleBufferGetPresentationTimeStamp(sb)
        let us = pts.isValid ? Int64(CMTimeGetSeconds(pts) * 1_000_000) : 0
        onFrame?(data, key, us, sps, pps)
    }

    private func parameterSet(_ fd: CMFormatDescription, _ i: Int) -> [UInt8]? {
        var p: UnsafePointer<UInt8>?
        var n = 0
        guard CMVideoFormatDescriptionGetH264ParameterSetAtIndex(fd, parameterSetIndex: i, parameterSetPointerOut: &p,
                                                                 parameterSetSizeOut: &n, parameterSetCountOut: nil,
                                                                 nalUnitHeaderLengthOut: nil) == noErr, let p = p else { return nil }
        return Array(UnsafeBufferPointer(start: p, count: n))
    }

    func stop() {
        if let s = session { VTCompressionSessionInvalidate(s) }
        session = nil
    }
}

final class ScreenCapture: NSObject, SCStreamOutput, SCStreamDelegate {
    let displayID: CGDirectDisplayID
    let width: Int, height: Int, fps: Int
    let withAudio: Bool
    let encoder: H264Encoder
    var onAudio: (([UInt8]) -> Void)?
    var onStopped: ((Error) -> Void)?

    private var stream: SCStream?
    private let videoQueue = DispatchQueue(label: "castlink.video", qos: .userInteractive)
    private let audioQueue = DispatchQueue(label: "castlink.audio", qos: .userInteractive)
    private var last: CVPixelBuffer?
    private var lastPts = CMTime.zero
    private var keyWanted = false

    init(displayID: CGDirectDisplayID, width: Int, height: Int, fps: Int, audio: Bool, bitrate: Int) throws {
        self.displayID = displayID
        self.width = width
        self.height = height
        self.fps = fps
        self.withAudio = audio
        self.encoder = try H264Encoder(width: width, height: height, fps: fps, bitrate: bitrate)
    }

    func start() async throws {
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
        guard let d = content.displays.first(where: { $0.displayID == displayID }) else {
            throw NSError(domain: "CastLink", code: 2, userInfo: [NSLocalizedDescriptionKey: "screen \(displayID) not found"])
        }
        let filter = SCContentFilter(display: d, excludingWindows: [])
        let cfg = SCStreamConfiguration()
        cfg.width = width
        cfg.height = height
        cfg.minimumFrameInterval = CMTime(value: 1, timescale: CMTimeScale(fps))
        cfg.pixelFormat = kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange
        cfg.showsCursor = true
        cfg.queueDepth = 5
        if withAudio {
            cfg.capturesAudio = true
            cfg.sampleRate = 48000
            cfg.channelCount = 2
            cfg.excludesCurrentProcessAudio = true
        }
        let s = SCStream(filter: filter, configuration: cfg, delegate: self)
        try s.addStreamOutput(self, type: .screen, sampleHandlerQueue: videoQueue)
        if withAudio { try s.addStreamOutput(self, type: .audio, sampleHandlerQueue: audioQueue) }
        try await s.startCapture()
        stream = s
    }

    func stop() {
        stream?.stopCapture { _ in }
        stream = nil
        encoder.stop()
    }

    /// The next frame is a keyframe. A still screen sends no frames, so the last one is encoded
    /// again at once: a viewer that just connected must not wait for the screen to change.
    func requestKey() {
        videoQueue.async {
            self.encoder.forceKey = true
            if let pb = self.last {
                self.lastPts = CMTimeAdd(self.lastPts, CMTime(value: 1, timescale: 1000))
                self.encoder.encode(pb, pts: self.lastPts)
            }
        }
    }

    func stream(_ stream: SCStream, didOutputSampleBuffer sb: CMSampleBuffer, of type: SCStreamOutputType) {
        switch type {
        case .screen: screenFrame(sb)
        case .audio: audioFrame(sb)
        default: break
        }
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        onStopped?(error)
    }

    private func screenFrame(_ sb: CMSampleBuffer) {
        // only complete frames carry a picture (idle = the screen did not change)
        guard let att = CMSampleBufferGetSampleAttachmentsArray(sb, createIfNecessary: false) as? [[SCStreamFrameInfo: Any]],
              let raw = att.first?[.status] as? Int, SCFrameStatus(rawValue: raw) == .complete,
              let pb = CMSampleBufferGetImageBuffer(sb) else { return }
        var pts = CMSampleBufferGetPresentationTimeStamp(sb)
        if CMTimeCompare(pts, lastPts) <= 0 { pts = CMTimeAdd(lastPts, CMTime(value: 1, timescale: 1000)) }
        lastPts = pts
        last = pb
        encoder.encode(pb, pts: pts)
    }

    private func audioFrame(_ sb: CMSampleBuffer) {
        guard let fd = CMSampleBufferGetFormatDescription(sb),
              let asbd = CMAudioFormatDescriptionGetStreamBasicDescription(fd)?.pointee,
              asbd.mFormatID == kAudioFormatLinearPCM else { return }
        let isFloat = asbd.mFormatFlags & kAudioFormatFlagIsFloat != 0
        let nonInterleaved = asbd.mFormatFlags & kAudioFormatFlagIsNonInterleaved != 0
        let channels = Int(asbd.mChannelsPerFrame)
        guard isFloat, asbd.mBitsPerChannel == 32, channels >= 1 else { return }
        let frames = CMSampleBufferGetNumSamples(sb)
        var out = [UInt8](repeating: 0, count: frames * 4)
        try? sb.withAudioBufferList { list, _ in
            func put(_ i: Int, _ l: Float, _ r: Float) {
                let a = Int16(max(-1, min(1, l)) * 32767), b = Int16(max(-1, min(1, r)) * 32767)
                out[i * 4] = UInt8(truncatingIfNeeded: a); out[i * 4 + 1] = UInt8(truncatingIfNeeded: a >> 8)
                out[i * 4 + 2] = UInt8(truncatingIfNeeded: b); out[i * 4 + 3] = UInt8(truncatingIfNeeded: b >> 8)
            }
            if nonInterleaved {
                guard let l = list.first?.mData?.assumingMemoryBound(to: Float.self) else { return }
                let r = list.count > 1 ? list[1].mData?.assumingMemoryBound(to: Float.self) ?? l : l
                for i in 0..<frames { put(i, l[i], r[i]) }
            } else {
                guard let p = list.first?.mData?.assumingMemoryBound(to: Float.self) else { return }
                for i in 0..<frames { put(i, p[i * channels], p[i * channels + (channels > 1 ? 1 : 0)]) }
            }
        }
        onAudio?(out)
    }
}
