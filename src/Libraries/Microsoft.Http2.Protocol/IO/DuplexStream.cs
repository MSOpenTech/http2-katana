﻿using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Org.Mentalis.Security.Ssl;
using Microsoft.Http2.Protocol.EventArgs;
using Microsoft.Http2.Protocol.Utils;

namespace Microsoft.Http2.Protocol.IO
{
    /// <summary>
    /// This class is based on SecureSocket and represents input/output stream.
    /// </summary>
    public class DuplexStream : Stream
    {
        private StreamBuffer _writeBuffer;
        private StreamBuffer _readBuffer;
        private SecureSocket _socket;
        private bool _isClosed;
        private readonly bool _ownsSocket;
        private readonly object _closeLock;

        public override int ReadTimeout {
            get { return 60000; }
        }

        public bool IsSecure 
        { 
            get
            {
                return _socket.SecureProtocol == SecureProtocol.Ssl3
                       || _socket.SecureProtocol == SecureProtocol.Tls1;
            }
        }

        public DuplexStream(SecureSocket socket, bool ownsSocket = false)
        {
            _writeBuffer = new StreamBuffer(1024);
            _readBuffer = new StreamBuffer(1024);
            _ownsSocket = ownsSocket;
            _socket = socket;
            _isClosed = false;
            _closeLock = new object();

            Task.Run(() => PumpIncomingData());
        }

        /// <summary>
        /// Pumps the incoming data into read buffer and signal that data for reading is available then.
        /// </summary>
        /// <returns></returns>
        private async Task PumpIncomingData()
        {
            while (!_isClosed)
            {
                var tmpBuffer = new byte[1024];
                int received = 0;
                try
                {
                    received = await Task.Factory.FromAsync<int>(_socket.BeginReceive(tmpBuffer, 0, tmpBuffer.Length, SocketFlags.None, null, null),
                            _socket.EndReceive, TaskCreationOptions.None, TaskScheduler.Default);
                }
                catch (Org.Mentalis.Security.SecurityException)
                {
                    Http2Logger.LogInfo("Connection was closed by the remote endpoint");
                }
                catch (Exception)
                {
                    Http2Logger.LogInfo("Connection was lost. Closing io stream");
                    if (!_isClosed)
                    {
                        Close();
                    }

                    return;
                }
                //TODO Connection was lost
                if (received == 0)
                {
                    if (!_isClosed)
                    {
                        Close();
                    }
                    break;
                }

                _readBuffer.Write(tmpBuffer, 0, received);

                //Signal data available and it can be read
                if (OnDataAvailable != null)
                    OnDataAvailable(this, new DataAvailableEventArgs(tmpBuffer));
            }
        }

        /// <summary>
        /// Method receives bytes from socket until match predicate returns false.
        /// Usable for receiving headers. Header block finishes with \r\n\r\n
        /// </summary>
        /// <param name="timeout">The timeout.</param>
        /// <param name="match">The match.</param>
        /// <returns></returns>
        public bool WaitForDataAvailable(int timeout, Predicate<byte[]> match = null)
        {
            if (Available != 0)
            {
                return true;
            }
            
            bool result;

            using (var wait = new ManualResetEvent(false))
            {
                EventHandler<DataAvailableEventArgs> dataReceivedHandler = delegate (object sender, DataAvailableEventArgs args)
                {
                    var receivedBuffer = args.ReceivedBytes;
                    if (match != null && match.Invoke(receivedBuffer))
                    {
                        wait.Set();
                    }
                    else if (match == null)
                    {
                        wait.Set();
                    }
                };

                //TODO think about if wait was already disposed
                OnDataAvailable += dataReceivedHandler;

                result = wait.WaitOne(timeout);

                OnDataAvailable -= dataReceivedHandler;
            }
            return result;
        }

        public override void Flush()
        {
            if (_isClosed)
                return;

            if (_writeBuffer.Available == 0)
                return;

            var bufferLen = _writeBuffer.Available;
            var flushBuffer = new byte[bufferLen];
            _writeBuffer.Read(flushBuffer, 0, bufferLen);

            _socket.Send(flushBuffer, 0, flushBuffer.Length, SocketFlags.None);
        }

        public async override Task FlushAsync(CancellationToken cancellationToken)
        {
            if (_isClosed)
                return;

            if (cancellationToken.IsCancellationRequested)
                cancellationToken.ThrowIfCancellationRequested();

            if (_writeBuffer.Available == 0)
                return;

            var bufferLen = _writeBuffer.Available;
            var flushBuffer = new byte[bufferLen];
            _writeBuffer.Read(flushBuffer, 0, bufferLen);

            await Task.Factory.FromAsync<int>(_socket.BeginSend(flushBuffer, 0, flushBuffer.Length, SocketFlags.None, null, null),
                                                _socket.EndSend, TaskCreationOptions.None, TaskScheduler.Default);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotImplementedException();
        }

        public override void SetLength(long value)
        {
            throw new NotImplementedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_isClosed)
                return 0;

            if (!WaitForDataAvailable(ReadTimeout))
            {
                // TODO consider throwing appropriate timeout exception
                return 0;
            }

            return _readBuffer.Read(buffer, offset, count);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_isClosed)
                return;

            _writeBuffer.Write(buffer, offset, count);
        }

        // TODO to extension methods ?? + check for args
        public int Write(byte[] buffer)
        {
            if (_isClosed)
                return 0;

            _writeBuffer.Write(buffer, 0, buffer.Length);

            return buffer.Length;
        }

        public override void WriteByte(byte value)
        {
            if (_isClosed)
                return;

            Write(new [] {value}, 0, 1);
        }

        public async override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_isClosed)
                return;

            if (cancellationToken.IsCancellationRequested)
                cancellationToken.ThrowIfCancellationRequested();
            
            //Refactor. Do not use lambda
            await Task.Factory.StartNew(() => _writeBuffer.Write(buffer, offset, count));
        }

        public async override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_isClosed)
                return 0;

            if (cancellationToken.IsCancellationRequested)
                cancellationToken.ThrowIfCancellationRequested();

            //Refactor. Do not use lambda
            return await Task.Factory.StartNew(() => _readBuffer.Read(buffer, offset, count));
        }

        public int Available { get { return _readBuffer.Available; } }

        public override bool CanRead
        {
            get { return true; }
        }

        public override bool CanSeek
        {
            get { return false; }
        }

        public override bool CanWrite
        {
            get { return true; }
        }

        public override long Length
        {
            get { throw new NotImplementedException(); }
        }

        public override long Position
        {
            get { throw new NotImplementedException(); }
            set { throw new NotImplementedException(); }
        }

        private event EventHandler<DataAvailableEventArgs> OnDataAvailable; 

        public override void Close()
        {
            lock (_closeLock)
            {
                //Return instead of throwing exception because external code calls Close and 
                //it knows nothing about defined exception.
                if (_isClosed)
                    return;

                _isClosed = true;

                if (_ownsSocket && _socket != null)
                {
                    _socket.Close();
                    _socket = null;
                }

                base.Close();
            }
        }
    }
}
