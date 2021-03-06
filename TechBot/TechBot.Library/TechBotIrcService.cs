using System;
using System.Text;
using System.Collections;
using System.Threading;

using TechBot.IRCLibrary;

namespace TechBot.Library
{
    public class IrcServiceOutput : IServiceOutput
    {
        public void WriteLine(MessageContext context,
                              string message)
        {
            if (context is ChannelMessageContext)
            {
                Thread.Sleep (500);
                ChannelMessageContext channelContext = context as ChannelMessageContext;
                channelContext.Channel.Talk(message);
            }
            else if (context is UserMessageContext)
            {
                UserMessageContext userContext = context as UserMessageContext;
                userContext.User.Talk(message);
            }
            else
            {
                throw new InvalidOperationException(String.Format("Unhandled message context '{0}'",
                                                                  context.GetType()));
            }
        }
    }

    public class IrcTechBotService : TechBotService
	{
        private int port;
		private string hostname;
		private string channelnames;
		private string botname;
		private string password;
		private IrcClient m_IrcClient;
		private ArrayList channels = new ArrayList();

        public IrcTechBotService(string hostname,
		                  int port,
		                  string channelnames,
		                  string botname,
		                  string password)
            : base (new IrcServiceOutput())
		{
			this.hostname = hostname;
			this.port = port;
			this.channelnames = channelnames;
			this.botname = botname;
			if (password == null || password.Trim() == "")
				this.password = null;
			else
				this.password = password;
		}

        public override void Run()
        {
            //Call the base class
            base.Run();

            m_IrcClient = new IrcClient();
            m_IrcClient.Encoding = Encoding.GetEncoding("iso-8859-1");             
            m_IrcClient.OnConnect += new EventHandler(m_IrcClient_OnConnect);
            m_IrcClient.OnConnectionLost += new EventHandler(m_IrcClient_OnConnectionLost);
            m_IrcClient.OnDisconnect += new EventHandler(m_IrcClient_OnDisconnect);
            m_IrcClient.MessageReceived += new EventHandler<IrcMessageReceivedEventArgs>(client_MessageReceived);
            m_IrcClient.ChannelUserDatabaseChanged +=new EventHandler<ChannelUserDatabaseChangedEventArgs>(client_ChannelUserDatabaseChanged);
            Connect();
        }

        void m_IrcClient_OnConnect(object sender, EventArgs e)
        {
            Console.WriteLine("Connected...");
        }

        private void Connect()
        {
            Console.WriteLine("Connecting to {0} port {1}",
                                                   hostname,
                                                   port);
            while (!m_IrcClient.Connected)
            {
                try
                {
                    m_IrcClient.Connect(hostname, port);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                    Thread.Sleep(10000);
                }
            }            
            
            m_IrcClient.Register(botname, password, null);
            Console.WriteLine("Registered as {0}...", m_IrcClient.Nickname);

            /* Did we get the nick we wanted? */
            if (m_IrcClient.Nickname != botname)
            {
                /* there must have been an existing one, kill it */
                m_IrcClient.GhostNick(botname, password);;
            }

            JoinChannels();
        }

        void m_IrcClient_OnDisconnect(object sender, EventArgs e)
        {
            Console.WriteLine("Disconnected...");
        }

        void m_IrcClient_OnConnectionLost(object sender, EventArgs e)
        {
            //Dispose old connection
            Disconnect();

            //Sleep for 1 minute
            Thread.Sleep(1000 * 60);

            //Try to reconnect
            Connect();
        }

        private void Disconnect()
        {
            try
            {
                m_IrcClient.Disconnect();
            }
            catch (Exception)
            {
                //
            }
        }

		public void Stop()
		{
            PartChannels();
            m_IrcClient.Disconnect();
		}

		private void JoinChannels()
		{
			foreach (string channelname in channelnames.Split(new char[] { ';' }))
			{
				IrcChannel channel = m_IrcClient.JoinChannel(channelname);
				channels.Add(channel);
				System.Console.WriteLine(String.Format("Joined channel #{0}...",
				                                       channel.Name));
			}
		}

		private void PartChannels()
		{
			foreach (IrcChannel channel in channels)
			{
				m_IrcClient.PartChannel(channel, "Caught in the bitstream...");
				System.Console.WriteLine(String.Format("Parted channel #{0}...",
				                                       channel.Name));
			}
		}

		private string GetMessageSource(MessageContext context)
		{
			if (context is ChannelMessageContext)
			{
				ChannelMessageContext channelContext = context as ChannelMessageContext;
				return String.Format("#{0}",
				                     channelContext.Channel.Name);
			}
			else if (context is UserMessageContext)
			{
				UserMessageContext userContext = context as UserMessageContext;
				return userContext.User.Nickname;
			}
			else
			{
				throw new InvalidOperationException(String.Format("Unhandled message context '{0}'",
				                                                  context.GetType()));
			}
		}

		private void ExtractMessage(string parameters,
		                            out string message)
		{
			int startIndex = parameters.IndexOf(':');
			if (startIndex != -1)
			{
				message = parameters.Substring(startIndex + 1);
			}
			else
			{
				message = parameters;
			}
		}

		private bool GetChannelName(IrcMessage message,
		                           out string channelName)
		{
			if (message.Parameters == null || !message.Parameters.StartsWith("#"))
			{
				channelName = null;
				return false;
			}

			int index = message.Parameters.IndexOf(' ');
			if (index == -1)
				index = message.Parameters.Length;
			else
				index = index - 1;
			channelName = message.Parameters.Substring(1, index);
			return true;
		}

		private bool GetTargetNickname(IrcMessage message,
		                               out string nickname)
		{
			if (message.Parameters == null)
			{
				nickname = null;
				return false;
			}

			int index = message.Parameters.IndexOf(' ');
			if (index == -1)
				index = message.Parameters.Length;
			nickname = message.Parameters.Substring(0, index);
			Console.WriteLine("nickname: " + nickname);
			return true;
		}

		private bool ShouldAcceptMessage(IrcMessage message,
		                                 out MessageContext context)
		{
			if (message.Command.ToUpper().Equals("PRIVMSG"))
			{
				string channelName;
				string nickname;
				if (GetChannelName(message,
				                   out channelName))
				{
					foreach (IrcChannel channel in channels)
					{
						if (String.Equals(channel.Name, channelName,StringComparison.OrdinalIgnoreCase))
						{
							context = new ChannelMessageContext(channel);
							return true;
						}
					}
				}
				else if (GetTargetNickname(message,
				                           out nickname))
				{
					IrcUser targetUser = new IrcUser(m_IrcClient,
					                                 nickname);
					if (String.Compare(targetUser.Nickname, botname, true) == 0)
					{
						IrcUser sourceUser = new IrcUser(m_IrcClient,
						                                 message.PrefixNickname);
						context = new UserMessageContext(sourceUser);
						return true;
					}
				}
			}
			context = null;
			return false;
		}
				
		private void client_MessageReceived(object sender, IrcMessageReceivedEventArgs e)
		{
            IrcMessage message = e.Message;

			try
			{
				if (message.Command != null &&
				    message.Parameters != null)
				{
					string injectMessage;
					ExtractMessage(message.Parameters,
					               out injectMessage);
					MessageContext context;
					if (ShouldAcceptMessage(message,
					                        out context))
					{
						Console.WriteLine(String.Format("Injecting: {0} from {1}",
						                                injectMessage,
						                                GetMessageSource(context)));
						InjectMessage(context,
						              injectMessage);
					}
					else
					{
						Console.WriteLine("Received: " + message.Line);
					}
				}
				else
				{
					Console.WriteLine("Received: " + message.Line);
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine(String.Format("Exception: {0}", ex));
			}
		}
		
		private void client_ChannelUserDatabaseChanged(object sender, ChannelUserDatabaseChangedEventArgs e)
        {
            IrcChannel channel = e.Channel;
		}
	}
}
