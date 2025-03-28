﻿using VGAudio.Concentus.Common;
using VGAudio.Concentus.Structs;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VGAudio.Codecs.Opus;

namespace VGAudio.Concentus.Oggfile
{
    /// <summary>
    /// A class for writing audio data as an .opus Ogg stream, using an Opus encoder provided in the constructor.
    /// This will handle all of the buffering, packetization and Ogg container work in order to output standard-compliant
    /// .opus files that can be played universally. Note that this makes very basic assumptions about output files:
    /// - Only 1 elementary stream
    /// - Segments may not span pages
    /// </summary>
    public class OpusOggWriteStream
    {
        private const int FRAME_SIZE_MS = 20;

        //private OpusEncoder _encoder;
        private Stream _outputStream;
        private Crc _crc;
        private int _inputChannels;

        // Resampler parameters
        private SpeexResampler _resampler;
        private int _inputSampleRate;
        private int _encoderSampleRate;

        // Ogg page parameters
        private short[] _opusFrame;
        private int _opusFrameSamples;
        private int _opusFrameIndex;
        private int _sampleCount = int.MaxValue;
        private int _preSkipSamples = 0;
        private byte[] _currentHeader = new byte[400];
        private byte[] _currentPayload = new byte[65536];
        private int _headerIndex = 0;
        private int _payloadIndex = 0;
        private int _pageCounter = 0;
        private int _logicalStreamId = 0;
        private long _granulePosition = 0;
        private byte _lacingTableCount = 0;
        private byte _pageFrameCount = 0;
        private const int PAGE_FLAGS_POS = 5;
        private const int GRANULE_COUNT_POS = 6;
        private const int CHECKSUM_HEADER_POS = 22;
        private const int SEGMENT_COUNT_POS = 26;
        private bool _finalized = false;
        
        /// <summary>
        /// Constructs a stream that will accept PCM audio input, and automatically encode it to Opus and packetize it using Ogg,
        /// writing the output pages to an underlying stream (usually a file stream).
        /// You are allowed to change the encoding parameters mid-stream using the properties of the OpusEncoder; the only thing you
        /// cannot change is the sample rate and num# of channels.
        /// </summary>
        /// <param name="encoder">An opus encoder to use for output</param>
        /// <param name="outputStream">A base stream to accept the encoded ogg file output</param>
        /// <param name="fileTags">(optional) A set of tags to include in the encoded file</param>
        /// <param name="inputSampleRate">The actual real sample rate of your input data (NOT the encoder's sample rate).
        /// The opus encoder usually requires 48Khz input, but most MP3s and such will give you 44.1Khz. To get the
        /// sample rates to line up properly in this case, set the encoder to 48000 and pass inputSampleRate = 44100,
        /// and the write stream will perform resampling for you automatically (Note that resampling will slow down
        /// the encoding).</param>
        public OpusOggWriteStream(Stream outputStream, int sampleRate, int channelCount, int preSkip, int sampleCount = -1, OpusTags fileTags = null, int inputSampleRate = 0)
        {
            _inputSampleRate = inputSampleRate;
            if (_inputSampleRate == 0)
            {
                _inputSampleRate = sampleRate;
            }

            _logicalStreamId = new Random().Next();
            _encoderSampleRate = sampleRate;
            _inputChannels = channelCount;
            _outputStream = outputStream;
            _opusFrameIndex = 0;
            _granulePosition = 0;
            _opusFrameSamples = (int)((long)_encoderSampleRate * FRAME_SIZE_MS / 1000);
            _opusFrame = new short[_opusFrameSamples * _inputChannels];
            _crc = new Crc();
            _resampler = SpeexResampler.Create(_inputChannels, _inputSampleRate, _encoderSampleRate, 5);

            _preSkipSamples = preSkip;
            _sampleCount = sampleCount == -1 ? int.MaxValue : preSkip + sampleCount;

            BeginNewPage();
            WriteOpusHeadPage();
            WriteOpusTagsPage(fileTags);
        }

        /// <summary>
        /// Writes a buffer of PCM audio samples to the encoder and packetizer. Runs Opus encoding and potentially outputs one or more pages to the underlying Ogg stream.
        /// You can write any non-zero number of samples that you want here; there are no restrictions on length or packet boundaries
        /// </summary>
        public void WriteSamples(OpusFrame frame)
        {
            if (_finalized)
            {
                throw new InvalidOperationException("Cannot write new samples to Ogg file, the output stream is already closed!");
            }

            if (_pageFrameCount >= 50)
            {
                FinalizePage();
            }

            Array.Copy(frame.Data, 0, _currentPayload, _payloadIndex, frame.Length);
            int packetSize = frame.Length;
            _payloadIndex += packetSize;

            // Opus granules are measured in 48Khz samples. 
            // Since the framesize is fixed (20ms) and the sample rate doesn't change, this is basically a constant value
            _granulePosition += frame.SampleCount;

            // And update the lacing values in the header
            int segmentLength = packetSize;
            while (segmentLength >= 255)
            {
                segmentLength -= 255;
                _currentHeader[_headerIndex++] = 0xFF;
                _lacingTableCount++;
            }
            _currentHeader[_headerIndex++] = (byte)segmentLength;
            _lacingTableCount++;
            _pageFrameCount++;

            _opusFrameIndex = 0;
        }

        /// <summary>
        /// Call when you are finished encoding your file. This operation will close the underlying stream as well.
        /// </summary>
        public void Finish()
        {
            _currentHeader[PAGE_FLAGS_POS] = (byte)PageFlags.EndOfStream;

            FinalizePage();

            // Now close our output
            _outputStream.Flush();
            _outputStream.Dispose();
            _finalized = true;
        }

        /// <summary>
        /// Writes the Ogg page for OpusHead, containing encoder information
        /// </summary>
        private void WriteOpusHeadPage()
        {
            if (_payloadIndex != 0)
            {
                throw new InvalidOperationException("Must begin writing OpusHead on a new page!");
            }

            _payloadIndex += WriteValueToByteBuffer("OpusHead", _currentPayload, _payloadIndex);
            _currentPayload[_payloadIndex++] = 0x01; // Version number
            _currentPayload[_payloadIndex++] = (byte)_inputChannels; // Channel count

            _payloadIndex += WriteValueToByteBuffer((short)_preSkipSamples, _currentPayload, _payloadIndex); // Pre-skip.
            _payloadIndex += WriteValueToByteBuffer(_encoderSampleRate, _currentPayload, _payloadIndex); //Input sample rate
            short outputGain = 0;
            _payloadIndex += WriteValueToByteBuffer(outputGain, _currentPayload, _payloadIndex); // Output gain in Q8
            _currentPayload[_payloadIndex++] = 0x00; // Channel map (0 indicates mono/stereo config)
            // Write the payload as segment data
            _currentHeader[_headerIndex++] = (byte)_payloadIndex; // implicit assumption that this value will always be less than 255
            _lacingTableCount++;
            // Set page flag to start of logical stream
            _currentHeader[PAGE_FLAGS_POS] = (byte)PageFlags.BeginningOfStream;
            FinalizePage();
        }

        /// <summary>
        /// Writes an Ogg page for the OpusTags, given an input tag set
        /// </summary>
        /// <param name="tags"></param>
        private void WriteOpusTagsPage(OpusTags tags = null)
        {
            if (tags == null)
            {
                tags = new OpusTags();
            }

            if (string.IsNullOrEmpty(tags.Comment))
            {
                tags.Comment = CodecHelpers.GetVersionString();
            }

            if (_payloadIndex != 0)
            {
                throw new InvalidOperationException("Must begin writing OpusTags on a new page!");
            }

            // BUGBUG: Very long tags can overflow the page and corrupt the stream
            _payloadIndex += WriteValueToByteBuffer("OpusTags", _currentPayload, _payloadIndex);

            // write comment
            int stringLength = WriteValueToByteBuffer(tags.Comment, _currentPayload, _payloadIndex + 4);
            _payloadIndex += WriteValueToByteBuffer(stringLength, _currentPayload, _payloadIndex);
            _payloadIndex += stringLength;

            // capture the location of the tag count field to fill in later
            int numTagsIndex = _payloadIndex;
            _payloadIndex += 4;

            // write each tag. skipping empty or invalid ones
            int tagsWritten = 0;
            foreach (var kvp in tags.Fields)
            {
                if (string.IsNullOrEmpty(kvp.Key) || string.IsNullOrEmpty(kvp.Value))
                    continue;

                string tag = kvp.Key + "=" + kvp.Value;
                stringLength = WriteValueToByteBuffer(tag, _currentPayload, _payloadIndex + 4);
                _payloadIndex += WriteValueToByteBuffer(stringLength, _currentPayload, _payloadIndex);
                _payloadIndex += stringLength;
                tagsWritten++;
            }

            // Write actual tag count
            WriteValueToByteBuffer(tagsWritten, _currentPayload, numTagsIndex);

            // Write segment data, ensuring we can handle tags longer than 255 bytes
            int tagsSegmentSize = _payloadIndex;
            while (tagsSegmentSize >= 255)
            {
                _currentHeader[_headerIndex++] = 255;
                _lacingTableCount++;
                tagsSegmentSize -= 255;
            }
            _currentHeader[_headerIndex++] = (byte)tagsSegmentSize;
            _lacingTableCount++;

            FinalizePage();
        }

        /// <summary>
        /// Clears all buffers and prepares a new page with an empty header
        /// </summary>
        private void BeginNewPage()
        {
            _headerIndex = 0;
            _payloadIndex = 0;
            _lacingTableCount = 0;
            _pageFrameCount = 0;

            // Page begin keyword
            _headerIndex += WriteValueToByteBuffer("OggS", _currentHeader, _headerIndex);
            // Stream version 0
            _currentHeader[_headerIndex++] = 0x0;
            // Header flags
            _currentHeader[_headerIndex++] = (byte)PageFlags.None;
            // Granule position (for opus, it is the number of 48Khz pcm samples encoded)
            _headerIndex += WriteValueToByteBuffer(_granulePosition, _currentHeader, _headerIndex);
            // Logical stream serial number
            _headerIndex += WriteValueToByteBuffer(_logicalStreamId, _currentHeader, _headerIndex);
            // Page sequence number
            _headerIndex += WriteValueToByteBuffer(_pageCounter, _currentHeader, _headerIndex);
            // Checksum is initially zero
            _currentHeader[_headerIndex++] = 0x0;
            _currentHeader[_headerIndex++] = 0x0;
            _currentHeader[_headerIndex++] = 0x0;
            _currentHeader[_headerIndex++] = 0x0;
            // Number of segments, initially zero
            _currentHeader[_headerIndex++] = _lacingTableCount;
            // Segment table goes after this point, once we have packets in this page

            _pageCounter++;
        }

        /// <summary>
        /// If the number of segments is nonzero, finalizes the page into a contiguous buffer, calculates CRC, and writes the page to the output stream
        /// </summary>
        private void FinalizePage()
        {
            if (_finalized)
            {
                throw new InvalidOperationException("Cannot finalize page, the output stream is already closed!");
            }

            if (_lacingTableCount != 0)
            {
                _granulePosition = Math.Min(_granulePosition, _sampleCount);
                // Write the final segment count to the header
                _currentHeader[SEGMENT_COUNT_POS] = _lacingTableCount;
                // And the granule count for frames that finished on this page
                WriteValueToByteBuffer(_granulePosition, _currentHeader, GRANULE_COUNT_POS);
                // Calculate CRC and update the header
                _crc.Reset();
                for (int c = 0; c < _headerIndex; c++)
                {
                    _crc.Update(_currentHeader[c]);
                }
                for (int c = 0; c < _payloadIndex; c++)
                {
                    _crc.Update(_currentPayload[c]);
                }
                //Debug.WriteLine("Writing CRC " + _crc.Value);
                WriteValueToByteBuffer(_crc.Value, _currentHeader, CHECKSUM_HEADER_POS);
                // Write the page to the stream (TODO: Make sure this operation does not overflow any target stream buffers?)
                _outputStream.Write(_currentHeader, 0, _headerIndex);
                _outputStream.Write(_currentPayload, 0, _payloadIndex);
                // And reset the page
                BeginNewPage();
            }
        }

        private static int WriteValueToByteBuffer(int val, byte[] target, int targetOffset)
        {
            byte[] bytes = BitConverter.GetBytes(val);
            Array.Copy(bytes, 0, target, targetOffset, 4);
            return 4;
        }

        private static int WriteValueToByteBuffer(long val, byte[] target, int targetOffset)
        {
            byte[] bytes = BitConverter.GetBytes(val);
            Array.Copy(bytes, 0, target, targetOffset, 8);
            return 8;
        }

        private static int WriteValueToByteBuffer(uint val, byte[] target, int targetOffset)
        {
            byte[] bytes = BitConverter.GetBytes(val);
            Array.Copy(bytes, 0, target, targetOffset, 4);
            return 4;
        }

        private static int WriteValueToByteBuffer(short val, byte[] target, int targetOffset)
        {
            byte[] bytes = BitConverter.GetBytes(val);
            Array.Copy(bytes, 0, target, targetOffset, 2);
            return 2;
        }

        private static int WriteValueToByteBuffer(string val, byte[] target, int targetOffset)
        {
            if (string.IsNullOrEmpty(val))
                return 0;
            byte[] bytes = Encoding.UTF8.GetBytes(val);
            Array.Copy(bytes, 0, target, targetOffset, bytes.Length);
            return bytes.Length;
        }
    }
}
