using System;
using System.IO;
using System.Text;
using System.Collections;
using System.Net.Sockets;

namespace TechBot.IRCLibrary
{
	/// <summary>
    /// EventArgs that delivers an IRC message.
	/// </summary>
    public class IrcMessageReceivedEventArgs : EventArgs
    {
        IrcMessage m_message;
        public IrcMessage Message
        { get { return m_message; } set { m_message = value; } }
        public IrcMessageReceivedEventArgs(IrcMessage item)
        {
            Message = item;
        }
    }


	/// <summary>
	/// EventArgs that notifies if the user database for a channel has changed.
	/// </summary>
    public class ChannelUserDatabaseChangedEventArgs : EventArgs
    {
        IrcChannel m_channel;
        public IrcChannel Channel
        { get { return m_channel; } set { m_channel = value; } }
        public ChannelUserDatabaseChangedEventArgs(IrcChannel item)
        {
            Channel = item;
        }
    }

	/// <summary>
	/// An IRC client.
	/// </summary>
	public class IrcClient
	{
		/// <summary>
		/// Monitor when an IRC command is received.
		/// </summary>
		private class IrcCommandEventRegistration
		{
			/// <summary>
			/// IRC command to monitor.
			/// </summary>
			private string command;
			public string Command
			{
				get
				{
					return command;
				}
			}

			/// <summary>
			/// Handler to call when command is received.
			/// </summary>
            private EventHandler<IrcMessageReceivedEventArgs> handler;
            public EventHandler<IrcMessageReceivedEventArgs> Handler
			{
				get
				{
					return handler;
				}
			}

			/// <summary>
			/// Constructor.
			/// </summary>
			/// <param name="command">IRC command to monitor.</param>
			/// <param name="handler">Handler to call when command is received.</param>
			public IrcCommandEventRegistration(string command,
                EventHandler<IrcMessageReceivedEventArgs> handler)
			{
				this.command = command;
				this.handler = handler;
			}
		}



		/// <summary>
		/// A buffer to store lines of text.
		/// </summary>
		private class LineBuffer
		{
			/// <summary>
			/// Full lines of text in buffer.
			/// </summary>
			private ArrayList strings;

			/// <summary>
			/// Part of the last line of text in buffer.
			/// </summary>
			private string left = "";

			/// <summary>
			/// Standard constructor.
			/// </summary>
			public LineBuffer()
			{
				strings = new ArrayList();
			}

			/// <summary>
			/// Return true if there is a complete line in the buffer or false if there is not.
			/// </summary>
			public bool DataAvailable
			{
				get
				{
					return (strings.Count > 0);
				}
			}

			/// <summary>
			/// Return next complete line in buffer or null if none exists.
			/// </summary>
			/// <returns>Next complete line in buffer or null if none exists.</returns>
			public string Read()
			{
				if (DataAvailable)
				{
					string line = strings[0] as string;
					strings.RemoveAt(0);
					return line;
				}
				else
				{
					return null;
				}
			}

			/// <summary>
			/// Write a string to buffer splitting it into lines.
			/// </summary>
			/// <param name="data"></param>
			public void Write(string data)
			{
				data = left + data;
				left = "";
				string[] sa = data.Split(new char[] { '\n' });
				if (sa.Length <= 0)
				{
					left = data;
					return;
				}
				else
				{
					left = "";
				}
				for (int i = 0; i < sa.Length; i++)
				{
					if (i < sa.Length - 1)
					{
						/* This is a complete line. Remove any \r characters at the end of the line. */
						string line = sa[i].TrimEnd(new char[] { '\r', '\n'});
						/* Silently ignore empty lines */
						if (!line.Equals(String.Empty))
						{
							strings.Add(line);
						}
					}
					else
					{
						/* This may be a partial line. */
						left = sa[i];
					}
				}
			}
		}


		/// <summary>
		/// State for asynchronous reads.
		/// </summary>
		private class StateObject
		{
			/// <summary>
			/// Network stream where data is read from.
			/// </summary>
			public NetworkStream Stream;

			/// <summary>
			/// Buffer where data is put.
			/// </summary>
			public byte[] Buffer;

			/// <summary>
			/// Constructor.
			/// </summary>
			/// <param name="stream">Network stream where data is read from.</param>
			/// <param name="buffer">Buffer where data is put.</param>
			public StateObject(NetworkStream stream, byte[] buffer)
			{
				this.Stream = stream;
				this.Buffer = buffer;
			}
		}


		#region Private fields
		private bool firstPingReceived = false;
		private bool awaitingGhostDeath = false;
		private System.Text.Encoding encoding = System.Text.Encoding.UTF8;
		private TcpClient tcpClient;
		private NetworkStream networkStream;
		private bool m_connected = false;
		private LineBuffer messageStream;
		private ArrayList ircCommandEventRegistrations = new ArrayList();
		private ArrayList channels = new ArrayList();
		private string reqNickname;
		private string curNickname;
		private string password;
		#endregion

		#region Public events

        public event EventHandler<IrcMessageReceivedEventArgs> MessageReceived;

        public event EventHandler<ChannelUserDatabaseChangedEventArgs> ChannelUserDatabaseChanged;

        public event EventHandler OnConnect;
        public event EventHandler OnConnectionLost;
        public event EventHandler OnDisconnect;

		#endregion

		#region Public properties

		/// <summary>
		/// Encoding used.
		/// </summary>
		public System.Text.Encoding Encoding
		{
			get
			{
				return encoding;
			}
			set
			{
				encoding = value;
			}
		}

		/// <summary>
		/// List of joined channels.
		/// </summary>
		public ArrayList Channels
		{
			get
			{
				return channels;
			}
		}

		/// <summary>
		/// Nickname for the bot.
		/// </summary>
		public string Nickname
		{
			get
			{
				return curNickname;
			}
		}

        /// <summary>
        /// Gets the client connection status.
        /// </summary>
        public bool Connected
        {
            get { return m_connected; }
        }
		#endregion

		#region Private methods

		/// <summary>
		/// Signal MessageReceived event.
		/// </summary>
		/// <param name="message">Message that was received.</param>
		private void OnMessageReceived(IrcMessage message)
		{
			foreach (IrcCommandEventRegistration icre in ircCommandEventRegistrations)
			{
				if (message.Command.ToLower().Equals(icre.Command.ToLower()))
				{
					icre.Handler(this, new IrcMessageReceivedEventArgs(message));
				}
			}
			if (MessageReceived != null)
			{
				MessageReceived(this, new IrcMessageReceivedEventArgs(message));
			}
		}

		/// <summary>
		/// Signal ChannelUserDatabaseChanged event.
		/// </summary>
		/// <param name="channel">Message that was received.</param>
		private void OnChannelUserDatabaseChanged(IrcChannel channel)
		{
			if (ChannelUserDatabaseChanged != null)
			{
				ChannelUserDatabaseChanged(this, new ChannelUserDatabaseChangedEventArgs(channel));
			}
		}

		/// <summary>
		/// Start an asynchronous read.
		/// </summary>
		private void Receive()
		{
			if ((networkStream != null) && (networkStream.CanRead))
			{
				byte[] buffer = new byte[1024];
				networkStream.BeginRead(buffer, 0, buffer.Length, 
					new AsyncCallback(ReadComplete),
					new StateObject(networkStream, buffer));
			}
			else
			{
                SocketException ex = (SocketException)new System.ComponentModel.Win32Exception("Cannot read and write from socket.");
                throw ex;
			}
		}

		/// <summary>
		/// Asynchronous read has completed.
		/// </summary>
		/// <param name="ar">IAsyncResult object.</param>
		private void ReadComplete(IAsyncResult ar)
		{
            try
            {
                StateObject stateObject = (StateObject)ar.AsyncState;
                if (stateObject.Stream.CanRead)
                {
                    int bytesReceived = stateObject.Stream.EndRead(ar);
                    if (bytesReceived > 0)
                    {
                        messageStream.Write(Encoding.GetString(stateObject.Buffer, 0, bytesReceived));
                        while (messageStream.DataAvailable)
                        {
                            string message = messageStream.Read();
                            OnMessageReceived(new IrcMessage(message));
                        }
                    }
                    else // 0 bytes read, the remote host has shut down the connection.
                    {
                        if (OnConnectionLost != null)
                            OnConnectionLost(this, EventArgs.Empty);
                    }
                }
             
                Receive();
            }
            catch (SocketException)
            {
                if (OnConnectionLost != null)
                    OnConnectionLost(this, EventArgs.Empty);
            }
            catch (IOException)
            {
                if (OnConnectionLost != null)
                    OnConnectionLost(this, EventArgs.Empty);
            }
            catch (Exception)
            {
                if (OnConnectionLost != null)
                    OnConnectionLost(this, EventArgs.Empty);
            } 
		}

		/// <summary>
		/// Locate channel.
		/// </summary>
		/// <param name="name">Channel name.</param>
		/// <returns>Channel or null if none was found.</returns>
		private IrcChannel LocateChannel(string name)
		{
			foreach (IrcChannel channel in Channels)
			{
				if (name.ToLower().Equals(channel.Name.ToLower()))
				{
					return channel;
				}
			}
			return null;
		}

		/// <summary>
		/// Send a PONG message when a PING message is received.
		/// </summary>
		/// <param name="message">Received IRC message.</param>
		private void PingMessageReceived(object sender, IrcMessageReceivedEventArgs e)
		{
            IrcMessage message = e.Message;
			SendMessage(new IrcMessage(IRC.PONG, message.Parameters));
			firstPingReceived = true;
		}

			/// <summary>
		/// Send a PONG message when a PING message is received.
		/// </summary>
		/// <param name="message">Received IRC message.</param>
        private void NoticeMessageReceived(object sender, IrcMessageReceivedEventArgs e)
        {
            IrcMessage message = e.Message;
            if (awaitingGhostDeath)
			{
				string str = string.Format("{0} has been ghosted", reqNickname);
				if (message.Parameters.Contains(str))
				{
					ChangeNick(reqNickname);
					SubmitPassword(password);
					awaitingGhostDeath = false;
				}
			}
		}

		/// <summary>
		/// Process RPL_NAMREPLY message.
		/// </summary>
		/// <param name="message">Received IRC message.</param>
        private void RPL_NAMREPLYMessageReceived(object sender, IrcMessageReceivedEventArgs e)
        {
            IrcMessage message = e.Message;
            try
			{
				// :Oslo2.NO.EU.undernet.org 353 E101 = #E101 :E101 KongFu_uK @Exception
				/* "( "=" / "*" / "@" ) <channel>
				   :[ "@" / "+" ] <nick> *( " " [ "@" / "+" ] <nick> )
					- "@" is used for secret channels, "*" for private
					channels, and "=" for others (public channels). */
				if (message.Parameters == null)
				{
					System.Diagnostics.Debug.WriteLine(String.Format("Message has no parameters."));
					return;
				}
				string[] parameters = message.Parameters.Split(new char[] { ' '});
				if (parameters.Length < 5)
				{
					System.Diagnostics.Debug.WriteLine(String.Format("{0} is two few parameters.", parameters.Length));
					return;
				}
				IrcChannelType type;
				switch (parameters[1])
				{
					case "=":
						type = IrcChannelType.Public;
						break;
					case "*":
						type = IrcChannelType.Private;
						break;
					case "@":
						type = IrcChannelType.Secret;
						break;
					default:
						type = IrcChannelType.Public;
						break;
				}
				IrcChannel channel = LocateChannel(parameters[2].Substring(1));
				if (channel == null)
				{
					System.Diagnostics.Debug.WriteLine(String.Format("Channel not found '{0}'.",
						parameters[2].Substring(1)));
					return;
				}
				else
					channel.Type = type;
				string nickname = parameters[3];
				if (nickname[0] != ':')
				{
					System.Diagnostics.Debug.WriteLine(String.Format("String should start with : and not {0}.", nickname[0]));
					return;
				}
				/* Skip : */
				IrcUser user = channel.LocateUser(nickname.Substring(1));
				if (user == null)
				{
					user = new IrcUser(this,
					                   nickname.Substring(1));
					channel.Users.Add(user);
				}
				for (int i = 4; i < parameters.Length; i++)
				{
					nickname = parameters[i];
					user = channel.LocateUser(nickname);
					if (user == null)
					{
						user = new IrcUser(this,
						                   nickname);
						channel.Users.Add(user);
					}
				}
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine(String.Format("Ex. {0}", ex));
			}
		}

		/// <summary>
		/// Process RPL_ENDOFNAMES message.
		/// </summary>
		/// <param name="message">Received IRC message.</param>
        private void RPL_ENDOFNAMESMessageReceived(object sender, IrcMessageReceivedEventArgs e)
        {
            IrcMessage message = e.Message;
			try
			{
				/* <channel> :End of NAMES list */
				if (message.Parameters == null)
				{
					System.Diagnostics.Debug.WriteLine(String.Format("Message has no parameters."));
					return;
				}

				string[] parameters = message.Parameters.Split(new char[] { ' ' });
				IrcChannel channel = LocateChannel(parameters[1].Substring(1));
				if (channel == null)
				{
					System.Diagnostics.Debug.WriteLine(String.Format("Channel not found '{0}'.",
						parameters[0].Substring(1)));
					return;
				}

				OnChannelUserDatabaseChanged(channel);
			}	
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine(String.Format("Ex. {0}", ex));
			}
		}

		/// <summary>
		/// Process ERR_NICKNAMEINUSE message.
		/// </summary>
		/// <param name="message">Received IRC message.</param>
        private void ERR_NICKNAMEINUSEMessageReceived(object sender, IrcMessageReceivedEventArgs e)
        {
            IrcMessage message = e.Message;
			try
			{
				if (message.Parameters == null)
				{
					System.Diagnostics.Debug.WriteLine(String.Format("Message has no parameters."));
					return;
				}
				
				/* Connect with a different name */
				string[] parameters = message.Parameters.Split(new char[] { ' ' });
				string nickname = parameters[1];
				ChangeNick(nickname + "__");
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine(String.Format("Ex. {0}", ex));
			}
		}

		#endregion

		/// <summary>
		/// Connect to the specified IRC server on the specified port.
		/// </summary>
		/// <param name="server">Address of IRC server.</param>
		/// <param name="port">Port of IRC server.</param>
		public void Connect(string server, int port)
		{
			if (m_connected)
			{
				throw new AlreadyConnectedException();
			}
			else
			{
				messageStream = new LineBuffer();
				tcpClient = new TcpClient();
				tcpClient.Connect(server, port);
				tcpClient.NoDelay = true;
				tcpClient.LingerState = new LingerOption(false, 0);
				networkStream = tcpClient.GetStream();
				m_connected = networkStream.CanRead && networkStream.CanWrite;
				if (!m_connected)
				{
                    SocketException ex = (SocketException)new System.ComponentModel.Win32Exception("Cannot read and write from socket.");
                    throw ex;
				}
				/* Install PING message handler */
                MonitorCommand(IRC.PING, new EventHandler<IrcMessageReceivedEventArgs>(PingMessageReceived));
				/* Install NOTICE message handler */
                MonitorCommand(IRC.NOTICE, new EventHandler<IrcMessageReceivedEventArgs>(NoticeMessageReceived));
				/* Install RPL_NAMREPLY message handler */
                MonitorCommand(IRC.RPL_NAMREPLY, new EventHandler<IrcMessageReceivedEventArgs>(RPL_NAMREPLYMessageReceived));
				/* Install RPL_ENDOFNAMES message handler */
                MonitorCommand(IRC.RPL_ENDOFNAMES, new EventHandler<IrcMessageReceivedEventArgs>(RPL_ENDOFNAMESMessageReceived));
				/* Install ERR_NICKNAMEINUSE message handler */
                MonitorCommand(IRC.ERR_NICKNAMEINUSE, new EventHandler<IrcMessageReceivedEventArgs>(ERR_NICKNAMEINUSEMessageReceived));
				/* Start receiving data */
				Receive();
                if (OnConnect != null)
                    OnConnect(this, EventArgs.Empty);
			}
		}

		/// <summary>
		/// Disconnect from IRC server.
		/// </summary>
		public void Disconnect()
		{
			if (!m_connected)
			{
				throw new NotConnectedException();
			}
			else
			{
				m_connected = false;
				tcpClient.Close();
				tcpClient = null;

                if (OnDisconnect != null)
                    OnDisconnect(this, EventArgs.Empty);
			}
		}

		/// <summary>
		/// Send an IRC message.
		/// </summary>
		/// <param name="message">The message to be sent.</param>
		public void SendMessage(IrcMessage message)
		{
            try
            {
                if (!m_connected)
                {
                    throw new NotConnectedException();
                }

                /* Serialize sending messages */
                lock (typeof(IrcClient))
                {
                    NetworkStream networkStream = tcpClient.GetStream();
                    byte[] bytes = Encoding.GetBytes(message.Line);
                    networkStream.Write(bytes, 0, bytes.Length);
                    networkStream.Flush();
                }
            }
            catch (SocketException)
            {
                if (OnConnectionLost != null)
                    OnConnectionLost(this, EventArgs.Empty);
            }
            catch (IOException)
            {
                if (OnConnectionLost != null)
                    OnConnectionLost(this, EventArgs.Empty);
            }
            catch (Exception)
            {
                if (OnConnectionLost != null)
                    OnConnectionLost(this, EventArgs.Empty);
            } 
		}

		/// <summary>
		/// Monitor when a message with an IRC command is received.
		/// </summary>
		/// <param name="command">IRC command to monitor.</param>
		/// <param name="handler">Handler to call when command is received.</param>
        public void MonitorCommand(string command, EventHandler<IrcMessageReceivedEventArgs> handler)
		{
			if (command == null)
			{
				throw new ArgumentNullException("command", "Command cannot be null.");
			}
			if (handler == null)
			{
				throw new ArgumentNullException("handler", "Handler cannot be null.");
			}
			ircCommandEventRegistrations.Add(new IrcCommandEventRegistration(command, handler));
		}

		/// <summary>
		/// Talk to the channel.
		/// </summary>
		/// <param name="nickname">Nickname of user to talk to.</param>
		/// <param name="text">Text to send to the channel.</param>
		public void TalkTo(string nickname, string text)
		{
		}

		/// <summary>
		/// Change nickname.
		/// </summary>
		/// <param name="nickname">New nickname.</param>
		public void ChangeNick(string nickname)
		{
			if (nickname == null)
				throw new ArgumentNullException("nickname", "Nickname cannot be null.");

			Console.WriteLine("Changing nick to {0}\n", nickname);
			curNickname = nickname;

			/* NICK <nickname> [ <hopcount> ] */
			SendMessage(new IrcMessage(IRC.NICK, nickname));
		}

        /// <summary>
        /// Ghost nickname.
        /// </summary>
        /// <param name="nickname">Nickname.</param>
        public void GhostNick(string nickname,
                              string password)
        {
            if (nickname == null)
                throw new ArgumentNullException("nickname", "Nickname cannot be null.");

            if (password == null)
                throw new ArgumentNullException("password", "Password cannot be null.");

            awaitingGhostDeath = true;

            /* GHOST <nickname> <password> */
            SendMessage(new IrcMessage(IRC.GHOST, nickname + " " + password));
        }

		/// <summary>
		/// Submit password to identify user.
		/// </summary>
		/// <param name="password">Password of registered nick.</param>
		private void SubmitPassword(string password)
		{
			if (password == null)
				throw new ArgumentNullException("password", "Password cannot be null.");

			this.password = password;

			/* PASS <password> */
			SendMessage(new IrcMessage(IRC.PASS, password));
		}

		/// <summary>
		/// Register.
		/// </summary>
		/// <param name="nickname">New nickname.</param>
		/// <param name="password">Password. Can be null.</param>
		/// <param name="realname">Real name. Can be null.</param>
		public void Register(string nickname,
		                     string password,
		                     string realname)
		{
			if (nickname == null)
				throw new ArgumentNullException("nickname", "Nickname cannot be null.");
			reqNickname = nickname;
			firstPingReceived = false;
			if (password != null)
			{
				SubmitPassword(password);
			}
			ChangeNick(nickname);
			/* OLD: USER <username> <hostname> <servername> <realname> */
			/* NEW: USER <user> <mode> <unused> <realname> */
			SendMessage(new IrcMessage(IRC.USER, String.Format("{0} 0 * :{1}",
				nickname, realname != null ? realname : "Anonymous")));

			/* Wait for PING for up til 10 seconds */
			int timer = 0;
			while (!firstPingReceived && timer < 200)
			{
				System.Threading.Thread.Sleep(50);
				timer++;
			}
		}

		/// <summary>
		/// Join an IRC channel.
		/// </summary>
		/// <param name="name">Name of channel (without leading #).</param>
		/// <returns>New channel.</returns>
		public IrcChannel JoinChannel(string name)
		{
			IrcChannel channel = new IrcChannel(this, name);
			channels.Add(channel);
			/* JOIN ( <channel> *( "," <channel> ) [ <key> *( "," <key> ) ] ) / "0" */
			SendMessage(new IrcMessage(IRC.JOIN, String.Format("#{0}", name)));
			return channel;
		}

		/// <summary>
		/// Part an IRC channel.
		/// </summary>
		/// <param name="channel">IRC channel. If null, the user parts from all channels.</param>
		/// <param name="message">Part message. Can be null.</param>
		public void PartChannel(IrcChannel channel, string message)
		{
			/* PART <channel> *( "," <channel> ) [ <Part Message> ] */
			if (channel != null)
			{
				SendMessage(new IrcMessage(IRC.PART, String.Format("#{0}{1}",
					channel.Name, message != null ? String.Format(" :{0}", message) : "")));
				channels.Remove(channel);
			}
			else
			{
				string channelList = null;
				foreach (IrcChannel myChannel in Channels)
				{
					if (channelList == null)
					{
						channelList = "";
					}
					else
					{
						channelList += ",";
					}
					channelList += myChannel.Name;
				}
				if (channelList != null)
				{
					SendMessage(new IrcMessage(IRC.PART, String.Format("#{0}{1}",
						channelList, message != null ? String.Format(" :{0}", message) : "")));
					Channels.Clear();
				}
			}
		}
	}
}
