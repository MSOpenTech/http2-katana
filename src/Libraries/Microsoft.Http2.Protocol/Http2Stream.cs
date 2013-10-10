﻿using Microsoft.Http2.Protocol.Compression;
using Microsoft.Http2.Protocol.EventArgs;
using Microsoft.Http2.Protocol.Exceptions;
using Microsoft.Http2.Protocol.FlowControl;
using Microsoft.Http2.Protocol.Framing;
using Microsoft.Http2.Protocol.IO;
using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using Microsoft.Http2.Protocol.Utils;

namespace Microsoft.Http2.Protocol
{
    /// <summary>
    /// Class represents http2 stream.
    /// </summary>
    public class Http2Stream : IDisposable
    {
        #region Fields

        private readonly int _id;
        private StreamState _state;
        private readonly WriteQueue _writeQueue;
        private readonly ICompressionProcessor _compressionProc;
        private readonly FlowControlManager _flowCrtlManager;

        private readonly Queue<DataFrame> _unshippedFrames;
        private readonly object _unshippedDeliveryLock = new object();

        #endregion

        #region Events

        /// <summary>
        /// Occurs when stream was sent frame.
        /// </summary>
        public event EventHandler<FrameSentArgs> OnFrameSent;

        /// <summary>
        /// Occurs when stream closes.
        /// </summary>
        public event EventHandler<StreamClosedEventArgs> OnClose;

        #endregion

        #region Constructors

        //Incoming
        internal Http2Stream(HeadersList headers, int id,
                           WriteQueue writeQueue, FlowControlManager flowCrtlManager, 
                           ICompressionProcessor comprProc, int priority = Constants.DefaultStreamPriority)
            : this(id, writeQueue, flowCrtlManager, comprProc, priority)
        {

            if (headers == null)
                throw new ArgumentNullException("cannot create stream with null headers");

            Headers = headers;
        }

        //Outgoing
        internal Http2Stream(int id, WriteQueue writeQueue, FlowControlManager flowCrtlManager,
                           ICompressionProcessor comprProc, int priority = Constants.DefaultStreamPriority)
        {
            if (id <= 0)
                throw new ArgumentOutOfRangeException("invalid id for stream");

            if (writeQueue == null || flowCrtlManager == null || comprProc == null)
                throw new ArgumentNullException("writeQueue or flowControlManager or compr proc is null");

            if (priority < 0 || priority > Constants.MaxPriority)
                throw  new ArgumentOutOfRangeException("priority out of range");

            _id = id;
            Priority = priority;
            _writeQueue = writeQueue;
            _compressionProc = comprProc;
            _flowCrtlManager = flowCrtlManager;

            _unshippedFrames = new Queue<DataFrame>(16);
            Headers = new HeadersList();

            SentDataAmount = 0;
            ReceivedDataAmount = 0;
            IsFlowControlBlocked = false;
            IsFlowControlEnabled = _flowCrtlManager.IsFlowControlEnabled;
            WindowSize = _flowCrtlManager.StreamsInitialWindowSize;

            _flowCrtlManager.NewStreamOpenedHandler(this);
            OnFrameSent += (sender, args) => FramesSent++;
        }

        #endregion

        #region Properties
        public int Id
        {
            get { return _id; }
        }

        public int FramesSent { get; set; }
        public int FramesReceived { get; set; }

        public bool EndStreamSent
        {
            get { return (_state & StreamState.EndStreamSent) == StreamState.EndStreamSent; }
            set
            {
                Contract.Assert(value); 
                _state |= StreamState.EndStreamSent;

                if (EndStreamReceived)
                {
                    Dispose(ResetStatusCode.None);
                }
            }
        }
        public bool EndStreamReceived
        {
            get { return (_state & StreamState.EndStreamReceived) == StreamState.EndStreamReceived; }
            set
            {
                Contract.Assert(value); 
                _state |= StreamState.EndStreamReceived;

                if (EndStreamSent)
                {
                    Dispose(ResetStatusCode.None);
                }
            }
        }

        public int Priority { get; set; }

        public bool ResetSent
        {
            get { return (_state & StreamState.ResetSent) == StreamState.ResetSent; }
            set { Contract.Assert(value); _state |= StreamState.ResetSent; }
        }
        public bool ResetReceived
        {
            get { return (_state & StreamState.ResetReceived) == StreamState.ResetReceived; }
            set { Contract.Assert(value); _state |= StreamState.ResetReceived; }
        }

        public bool Disposed
        {
            get { return (_state & StreamState.Disposed) == StreamState.Disposed; }
            set { Contract.Assert(value); _state |= StreamState.Disposed; }
        }

        public HeadersList Headers { get; private set; }

        #endregion

        #region FlowControl

        public void UpdateWindowSize(Int32 delta)
        {
            if (IsFlowControlEnabled)
            {
                //06
                //A sender MUST NOT allow a flow control window to exceed 2^31 - 1
                //bytes.  If a sender receives a WINDOW_UPDATE that causes a flow
                //control window to exceed this maximum it MUST terminate either the
                //stream or the connection, as appropriate.  For streams, the sender
                //sends a RST_STREAM with the error code of FLOW_CONTROL_ERROR code;
                //for the connection, a GOAWAY frame with a FLOW_CONTROL_ERROR code.
                WindowSize += delta;

                if (WindowSize > Constants.MaxWindowSize)
                {
                    Http2Logger.LogDebug("Incorrect window size : {0}", WindowSize);
                    throw new ProtocolError(ResetStatusCode.FlowControlError, String.Format("Incorrect window size : {0}", WindowSize));
                }
            }

            //Unblock stream if it was blocked by flowCtrlManager
            if (WindowSize > 0 && IsFlowControlBlocked)
            {
                IsFlowControlBlocked = false;
            }
        }

        /// <summary>
        /// Pumps the unshipped frames.
        /// Calls after window update was received for stream. 
        /// Method tried to deliver as many unshipped data frames as it can.
        /// </summary>
        public void PumpUnshippedFrames()
        {
            if (Disposed)
                return;

            //Handle window update one at a time
            lock (_unshippedDeliveryLock)
            {
                while (_unshippedFrames.Count > 0 && IsFlowControlBlocked == false)
                {
                    var dataFrame = _unshippedFrames.Dequeue();
                    WriteDataFrame(dataFrame);
                }

                //Do not dispose if unshipped frames are still here
                if (EndStreamSent && EndStreamReceived && !Disposed)
                {
                    Dispose(ResetStatusCode.None);
                }
            }
        }

        public Int32 WindowSize { get; set; }

        public Int64 SentDataAmount { get; private set; }

        public Int64 ReceivedDataAmount { get; set; }

        public bool IsFlowControlBlocked { get; set; }

        public bool IsFlowControlEnabled { get; set; }
        #endregion

        #region WriteMethods

        public void WriteHeadersFrame(HeadersList headers, bool isEndStream, bool isEndHeaders)
        {
            if (headers == null)
                throw new ArgumentNullException("headers is null");

            if (Disposed)
                return;

            Headers.AddRange(headers);

            byte[] headerBytes = _compressionProc.Compress(headers);

            var frame = new HeadersFrame(_id, headerBytes, Priority)
                {
                    IsEndHeaders = isEndHeaders,
                    IsEndStream = isEndStream,
                };

            _writeQueue.WriteFrame(frame);

            if (frame.IsEndStream)
            {
                EndStreamSent = true;
            }

            if (OnFrameSent != null)
            {
                OnFrameSent(this, new FrameSentArgs(frame));
            }
        }

        /// <summary>
        /// Writes the data frame.
        /// If flow control manager has blocked stream, frames are adding to the unshippedFrames collection.
        /// After window update for that stream they will be delivered.
        /// </summary>
        /// <param name="data">The data.</param>
        /// <param name="isEndStream">if set to <c>true</c> [is fin].</param>
        public void WriteDataFrame(ArraySegment<byte> data, bool isEndStream)
        {
            if (data.Array == null)
                throw new ArgumentNullException("data is null");

            if (Disposed)
                return;

            var dataFrame = new DataFrame(_id, data, isEndStream);

            //We cant let lesser frame that were passed through flow control window
            //be sent before greater frames that were not passed through flow control window

            //06
            //The sender MUST NOT
            //send a flow controlled frame with a length that exceeds the space
            //available in either of the flow control windows advertised by the receiver.
            if (_unshippedFrames.Count != 0 || WindowSize - dataFrame.Data.Count < 0)
            {
                _unshippedFrames.Enqueue(dataFrame);
                return;
            }

            if (!IsFlowControlBlocked)
            {
                _writeQueue.WriteFrame(dataFrame);
                SentDataAmount += dataFrame.FrameLength;

                _flowCrtlManager.DataFrameSentHandler(this, new DataFrameSentEventArgs(dataFrame));

                if (dataFrame.IsEndStream)
                {
                    Http2Logger.LogDebug("Transfer end");
                    EndStreamSent = true;
                }

                if (OnFrameSent != null)
                {
                    OnFrameSent(this, new FrameSentArgs(dataFrame));
                }
            }
            else
            {
                _unshippedFrames.Enqueue(dataFrame);
            }
        }

        /// <summary>
        /// Writes the data frame. Method is used for pushing unshipped frames.
        /// If flow control manager has blocked stream, frames are adding to the unshippedFrames collection.
        /// After window update for that stream they will be delivered.
        /// </summary>
        /// <param name="dataFrame">The data frame.</param>
        private void WriteDataFrame(DataFrame dataFrame)
        {
            if (dataFrame == null)
                throw new ArgumentNullException("dataFrame is null");

            if (Disposed)
                return;

            if (!IsFlowControlBlocked)
            {
                _writeQueue.WriteFrame(dataFrame);
                SentDataAmount += dataFrame.FrameLength;

                _flowCrtlManager.DataFrameSentHandler(this, new DataFrameSentEventArgs(dataFrame));

                if (dataFrame.IsEndStream)
                {
                    Http2Logger.LogDebug("Transfer end");
                    EndStreamSent = true;
                }

                if (OnFrameSent != null)
                {
                    OnFrameSent(this, new FrameSentArgs(dataFrame));
                }
            }
            else
            {
                _unshippedFrames.Enqueue(dataFrame);
            }
        }

        public void WriteWindowUpdate(Int32 windowSize)
        {
            if (windowSize <= 0)
                throw new ArgumentOutOfRangeException("windowSize should be greater thasn 0");

            if (windowSize > Constants.MaxWindowSize)
                throw new ProtocolError(ResetStatusCode.FlowControlError, "window size is too large");

            //Spec 06
            //After a receiver reads in a frame that marks the end of a stream (for
            //example, a data stream with a END_STREAM flag set), it MUST cease
	        //transmission of WINDOW_UPDATE frames for that stream.
            if (Disposed || EndStreamReceived)
                return;

            var frame = new WindowUpdateFrame(_id, windowSize);
            _writeQueue.WriteFrame(frame);

            if (OnFrameSent != null)
            {
                OnFrameSent(this, new FrameSentArgs(frame));
            }
        }

        public void WriteRst(ResetStatusCode code)
        {
            if (Disposed)
                return;

            var frame = new RstStreamFrame(_id, code);
            
            _writeQueue.WriteFrame(frame);
            ResetSent = true;

            if (OnFrameSent != null)
            {
                OnFrameSent(this, new FrameSentArgs(frame));
            }
        }

        #endregion

        public void Dispose()
        {
            Dispose(ResetStatusCode.Cancel);
        }

        public void Dispose(ResetStatusCode code)
        {
            if (Disposed)
            {
                return;
            }

            OnFrameSent = null;

            Http2Logger.LogDebug("Total outgoing data frames volume " + SentDataAmount);
            Http2Logger.LogDebug("Total frames sent: {0}", FramesSent);
            Http2Logger.LogDebug("Total frames received: {0}", FramesReceived);

            if (code == ResetStatusCode.Cancel || code == ResetStatusCode.InternalError)
                WriteRst(code);

            _flowCrtlManager.StreamClosedHandler(this);

            Disposed = true;

            if (OnClose != null)
                OnClose(this, new StreamClosedEventArgs(_id));

            OnClose = null;

            Http2Logger.LogDebug("Stream closed " + _id);
        }
    }
}
