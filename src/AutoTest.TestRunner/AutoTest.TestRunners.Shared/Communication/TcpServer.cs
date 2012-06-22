using System;
using System.Net.Sockets;
using System.IO;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Linq;
using AutoTest.TestRunners.Shared.Logging;

namespace AutoTest.TestRunners.Shared.Communication
{
	public class MessageArgs : EventArgs
	{
		public string ClientID { get; private set; }
		public string Message { get; private set; }
		
		public MessageArgs(string clientID, string message)
		{
			ClientID = clientID;
			Message = message;
		}
	}
	
	public interface ITcpServer
	{
		event EventHandler ClientConnected;
		event EventHandler<MessageArgs> IncomingMessage;
		
		string Server { get; }
		int Port { get; }
		
		void Start();
		void Send(string message);
		void Send(string message, Guid clientID);
	}

	public class TcpServer
	{
		class Client
		{
			public bool IsRunner { get; private set; }
			public string ID { get; private set; }
			public NetworkStream Stream { get; private set; }

			public Client(NetworkStream stream)
			{
				ID = Guid.NewGuid().ToString();
				Stream = stream;
			}

			public void SetID(string id)
			{
				ID = id;
				IsRunner = true;
			}
		}

		private Socket _listener = null;
		private List<Client> _clients = new List<Client>();
		private byte[] _buffer = new byte[5000];
		private MemoryStream _readBuffer = new MemoryStream();
		private int _currentPort = 0;
		private string _messageTermination = null;
		private List<string> _unsentMessages = new List<string>();
		
		public event EventHandler ClientConnected;
		public event EventHandler<MessageArgs> IncomingMessage;
		
		public string Server { get { return "127.0.0.1"; } }
		public int Port { get { return _currentPort; } }
		
		public TcpServer()
		{
		}

		public TcpServer(string messageTermination)
		{
			_messageTermination = messageTermination;
		}
		
		public void Start(int port)
		{
            _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var ipEndpoint = new IPEndPoint(IPAddress.Parse(Server), port);
            _listener.Bind(ipEndpoint);
            _currentPort = ((IPEndPoint)_listener.LocalEndPoint).Port;
            _listener.Listen(1);
            _listener.BeginAccept(new AsyncCallback(AcceptCallback), _listener);
		}
		
		private void AcceptCallback(IAsyncResult result)
        {
            var listener = (Socket)result.AsyncState;
            try
            {
                var client = listener.EndAccept(result);
                var clientStream = new Client(new NetworkStream(client));
                lock (_clients)
                {
                    _clients.Add(clientStream);
                }
                clientStream.Stream.BeginRead(_buffer, 0, _buffer.Length, ReadCompleted, clientStream);
                if (ClientConnected != null)
					ClientConnected(this, new EventArgs());
            }
            catch (Exception ex)
            {
				Logger.Debug(ex);
            }
            finally
            {
                listener.BeginAccept(new AsyncCallback(AcceptCallback), listener);
            }
        }
		
		private void ReadCompleted(IAsyncResult result)
        {
            var stream = (Client) result.AsyncState;
            try
            {
                var x = stream.Stream.EndRead(result);
                if(x == 0) return;
                for (int i = 0; i < x;i++)
                {
					if (isEndOfMessage(i))
                    {
                        byte[] data = _readBuffer.ToArray();
                        string actual;
						if (_messageTermination == null)
							actual = Encoding.UTF8.GetString(data, 0, data.Length);
						else
						    actual = Encoding.UTF8.GetString(data, 0, data.Length - (_messageTermination.Length - 1));

						if (!isInternalMessage(stream, actual)) {
							forward(stream, actual);
						}
                        _readBuffer.SetLength(0);
                    }
                    else
                    {
                        _readBuffer.WriteByte(_buffer[i]);
                    }
                }
                stream.Stream.BeginRead(_buffer, 0, _buffer.Length, ReadCompleted, stream);
            }
            catch (Exception ex)
            {
				Logger.Debug(ex);
                disconnect(stream);
            }
        }

		private bool isInternalMessage(Client client, string message)
		{
			var prefix = "RunnerID:";
			if (message.StartsWith(prefix)) {
				client.SetID(message.Substring(prefix.Length, message.Length - prefix.Length));
				forward(client, "RunnerInitialized");
				// Send messages ment for this runner that was sent before it got initialized
				lock (_unsentMessages)
				{
					var messages = _unsentMessages
						.Where(x => 
							x == "exit" ||
							x.StartsWith(client.ID + ":") ||
							x.StartsWith(stripPluginID(client.ID) + "|any:")).ToList();
					messages.ForEach(x => Send(stripReceiver(x), client.ID));
					messages.RemoveAll(x => x.StartsWith(client.ID + ":"));
				}
				return true;
			}
			if (message == "exit") {
				exitAll();
				return true;
			}
			return false;
		}

		private void exitAll()
		{
			// Queue in unsent messages to stop runners currently spawning
			lock (_unsentMessages) {
				_unsentMessages.Add("exit");
			}
			_clients
				.Where(x => x.IsRunner).ToList()
				.ForEach(x => Send("exit", x.ID));
		}

		private void forward(Client client, string message)
		{
			if (client.IsRunner) {
				_clients
					.Where(x => !x.IsRunner).ToList()
					.ForEach(x => Send(client.ID + ":" + message, x.ID));
			} else {
				lock (_unsentMessages)
				{
					var clients = _clients
						.Where(x => 
							x.IsRunner &&
							(message == "exit" ||
							 message.StartsWith(x.ID + ":") ||
							 message.StartsWith(stripPluginID(x.ID) + "|any:"))).ToList();
					if (clients.Count > 0)
						clients.ForEach(x => Send(stripReceiver(message), x.ID));
					else
						_unsentMessages.Add(message);
				}
			}
		}

		private string stripPluginID(string message)
		{
			if (message.IndexOf("|") == -1)
				return message;
			var end = message.IndexOf("|");
			return message.Substring(0, end);
		}

		private string stripReceiver(string message)
		{
			if (message.IndexOf(":") == -1)
				return message;
			var start = message.IndexOf(":") + 1;
			return message.Substring(start, message.Length - start);
		}
		
		private bool isEndOfMessage(int index)
		{
			if (_messageTermination == null)
				return _buffer[index].Equals(0);
			if (_messageTermination.Length > (index + 1))
				return false;
			for (int i = index; i > (index - _messageTermination.Length); i--)
			{
				if (!Encoding.UTF8.GetString(new byte[] { _buffer[i]}).Equals(_messageTermination.Substring(_messageTermination.Length - (index - i) - 1, 1)))
					return false;
			}
			return true;
		}
		
		private void disconnect(Client stream)
		{
			lock(_clients)
			{
				_clients.Remove(stream);
			}
		}
		
		public void Send(string message)
        {
            lock (_clients)
			{
				// Add message terminate char
				byte[] data;
				if (_messageTermination == null)
					data = Encoding.UTF8.GetBytes(message).Concat(new byte[] { 0x0 }).ToArray();
				else
					data = Encoding.UTF8.GetBytes(message + _messageTermination).ToArray();
                SendToClients(data);
            }
        }

		public void Send(string message, string clientID)
		{
			lock (_clients)
			{
				// Add message terminate char
				byte[] data;
				if (_messageTermination == null)
					data = Encoding.UTF8.GetBytes(message).Concat(new byte[] { 0x0 }).ToArray();
				else
					data = Encoding.UTF8.GetBytes(message + _messageTermination).ToArray();
				
				var client = _clients.FirstOrDefault(x => x.ID.Equals(clientID));
				if (client == null)
					return;
				try
				{
					sendToClient(data, client);
				}
				catch
				{
					disconnect(client);
				}
			}
		}

        private void SendToClients(byte[] data)
        {
			var failingClients = new List<Client>();
            foreach (var client in _clients)
            {
                try
                {
					sendToClient(data, client);
                }
                catch
                {
                    failingClients.Add(client);
                }
            }
			failingClients.ForEach(client => disconnect(client));
        }

		private void sendToClient(byte[] data, Client client)
		{
			var stream = client.Stream;
            stream.BeginWrite(data, 0, data.Length, WriteCompleted, client);
		}
		
		private void WriteCompleted(IAsyncResult result)
        {
            var client = (Client) result.AsyncState;
            try
            {
                client.Stream.EndWrite(result);
            }
            catch (Exception ex)
            {
				Logger.Debug(ex);
                disconnect(client);
            }
        }
	}
}