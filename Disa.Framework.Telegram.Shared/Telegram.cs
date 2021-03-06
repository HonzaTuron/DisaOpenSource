﻿using System;
using System.CodeDom;
using Disa.Framework.Bubbles;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using SharpTelegram;
using SharpMTProto;
using SharpMTProto.Transport;
using SharpMTProto.Authentication;
using SharpTelegram.Schema;
using System.Linq;
using SharpMTProto.Messaging.Handlers;
using SharpMTProto.Schema;
using System.Globalization;
using System.Timers;
using System.IO;
using System.Reactive;
using System.Security.Cryptography;
using System.Drawing;
using ProtoBuf;
using IMessage = SharpTelegram.Schema.IMessage;
using Message = SharpTelegram.Schema.Message;

//TODO:
//1) After authorization, there's an expiry time. Ensure that the login expires by then (also, in DC manager)

namespace Disa.Framework.Telegram
{
    [ServiceInfo("Telegram", true, true, true, false, true, typeof(TelegramSettings),
        ServiceInfo.ProcedureType.ConnectAuthenticate, typeof(TextBubble), typeof(ReadBubble),
                 typeof(TypingBubble), typeof(PresenceBubble), typeof(ImageBubble), typeof(FileBubble), typeof(AudioBubble), typeof(LocationBubble), typeof(ContactBubble))]
    [FileParameters(1000000000)] //1GB
    [AudioParameters(AudioParameters.RecordType.M4A, AudioParameters.NoDurationLimit, 25000000, ".mp3", ".aac", ".m4a", ".mp4", ".wav", ".3ga", ".3gp", ".3gpp", ".amr", ".ogg", ".webm", ".weba", ".opus")]
    public partial class Telegram : Service, IVisualBubbleServiceId, ITerminal
    {
        private object _cachedThumbnailsLock = new object();

        private static TcpClientTransportConfig DefaultTransportConfig = 
            new TcpClientTransportConfig("149.154.167.50", 443);

        private readonly object _baseMessageIdCounterLock = new object();
        private string _baseMessageId = "0000000000";
        private int _baseMessageIdCounter;

        private List<User> contactsCache = new List<User>();

        public bool LoadConversations;

        private object _quickUserLock = new object();

        private TelegramClient cachedClient;

        private bool _oldMessages = false;

		private object _globalBubbleLock = new object();
        //msgid, address, date
        private Dictionary<uint,Tuple<uint,uint,bool>> messagesUnreadCache = new Dictionary<uint, Tuple<uint, uint,bool>>();

        public string CurrentMessageId
        {
            get
            {
                return _baseMessageId + Convert.ToString(_baseMessageIdCounter);
            }
        }

        public string NextMessageId
        {
            get
            {
                lock (_baseMessageIdCounterLock)
                {
                    _baseMessageIdCounter++;
                    return CurrentMessageId;
                }
            }
        }

        public Config Config
        {
            get 
            {
                return _config;
            }
        }

        public TelegramSettings Settings
        {
            get
            {
                return _settings;   
            }
        }

        public CachedDialogs Dialogs
        {
            get
            {
                return _dialogs;
            }
        }

        private bool _hasPresence;

        private bool _longPollerAborted;

        private Random _random = new Random(System.Guid.NewGuid().GetHashCode());

        private TelegramSettings _settings;

        private TelegramMutableSettings _mutableSettings;

        private TelegramClient _longPollClient;

        private readonly object _mutableSettingsLock = new object();
        
        private CachedDialogs _dialogs = new CachedDialogs();

        private Config _config;

        private Dictionary<string, Timer> _typingTimers = new Dictionary<string, Timer>();

        private ConcurrentDictionary<string, bool> _thumbnailDownloadingDictionary = new ConcurrentDictionary<string, bool>();

		private ConcurrentDictionary<string, DisaThumbnail> _thumbnailCache = new ConcurrentDictionary<string, DisaThumbnail>();

        private WakeLockBalancer.GracefulWakeLock _longPollHeartbeart;

        private string _thumbnailDatabasePathCached;

        private void CancelTypingTimer(string id)
        {
            if (_typingTimers.ContainsKey(id))
            {
                var timer = _typingTimers[id];
                timer.Stop();
                timer.Dispose();
            }
        }

        public void SaveState(uint date, uint pts, uint qts, uint seq)
        {
            lock (_mutableSettingsLock)
            {
				DebugPrint("Saving new state date " + date + " pts " + pts + " qts " + qts + " seq " + seq);;
                if (date != 0)
                {
                    _mutableSettings.Date = date;
                }
                if (pts != 0)
                {
                    _mutableSettings.Pts = pts;
                }
                if (qts != 0)
                {
                    _mutableSettings.Qts = qts;
                }
                if (seq != 0)
                {
                    _mutableSettings.Seq = seq;
                }
                MutableSettingsManager.Save(_mutableSettings);
            }
        }

        private object NormalizeUpdateIfNeeded(object obj)
        {
            // flatten UpdateNewMessage to Message
            var newMessage = obj as UpdateNewMessage;
            var newChannelMessage = obj as UpdateNewChannelMessage;
            if (newMessage != null)
            {
                return newMessage.Message;
            }
            if (newChannelMessage != null)
            {
                var message = newChannelMessage.Message as Message;
                if (message != null)
                {
                    var peerChannel = message.ToId as PeerChannel;
                    uint channelId = peerChannel.ChannelId;
                    SaveChannelState(channelId, newChannelMessage.Pts); 
                }
                return newChannelMessage.Message;
            }
            return obj;
        }

        private void SaveChannelState(uint channelId, uint pts)
        {
            if (pts == 0)
                return;
            _dialogs.UpdateChatPts(channelId, pts);
        }

        private List<object> AdjustUpdates(List<object> updates)
        {
            if (updates == null)
            {
                return null;
            }
            var precedents = new List<object>();
            var successors = updates.ToList();
            foreach (var update in successors.ToList())
            {
                var user = update as IUser;
                var chat = update as IChat;
                if (user != null || chat != null)
                {
                    precedents.Add(update);
                    successors.Remove(update);
                }
            }
            return precedents.Concat(successors).ToList();
        }

        public override Task GetQuotedMessageTitle(VisualBubble bubble, Action<string> result)
        {
            return Task.Factory.StartNew(() =>
            {
                var userId = bubble.QuotedAddress;
                if (userId != null)
                {
                    DebugPrint("#### got user id for quoted title " + userId);
                    uint userIdInt = uint.Parse(userId);
                    string username = TelegramUtils.GetUserName(_dialogs.GetUser(userIdInt));
                    result(username);
                }

            });
        }

		public void TelegramEventBubble(Bubble bubble)
		{ 
			lock(_globalBubbleLock)
			{
				EventBubble(bubble);
			}
		}

        private void ProcessIncomingPayload(List<object> payloads, bool useCurrentTime, TelegramClient optionalClient = null)
        {
            uint maxMessageId = 0;

            foreach (var payload in AdjustUpdates(payloads))
            {
                var update = NormalizeUpdateIfNeeded(payload);

                var shortMessage = update as UpdateShortMessage;
                var shortChatMessage = update as UpdateShortChatMessage;
                var typing = update as UpdateUserTyping;
                var typingChat = update as UpdateChatUserTyping;
                var userStatus = update as UpdateUserStatus;
                var messageService = update as MessageService;
                var updateChatParticipants = update as UpdateChatParticipants;
                var updateContactRegistered = update as UpdateContactRegistered;
                var updateContactLink = update as UpdateContactLink;
                var updateUserPhoto = update as UpdateUserPhoto;
                var updateReadHistoryInbox = update as UpdateReadHistoryInbox;
                var updateReadHistoryOutbox = update as UpdateReadHistoryOutbox;
                var updateReadChannelInbox = update as UpdateReadChannelInbox;
                var updateReadChannelOutbox = update as UpdateReadChannelOutbox;
                var updateChannelTooLong = update as UpdateChannelTooLong;
                var updateDeleteChannelMessage = update as UpdateDeleteChannelMessages;
                var updateEditChannelMessage = update as UpdateEditChannelMessage;
                var updateChatAdmins = update as UpdateChatAdmins;
                var updateChannel = update as UpdateChannel;
                var message = update as SharpTelegram.Schema.Message;
                var user = update as IUser;
                var chat = update as IChat;

                DebugPrint(">>>>>> The type of object in process incoming payload is " + ObjectDumper.Dump(update));

                if (shortMessage != null)
                {
                    if (!string.IsNullOrWhiteSpace(shortMessage.Message))
                    {
                        var fromId = shortMessage.UserId.ToString(CultureInfo.InvariantCulture);
                        var shortMessageUser = _dialogs.GetUser(shortMessage.UserId);
                        if (shortMessageUser == null)
                        {
                            DebugPrint(">>>>> User is null, fetching user from the server");
                            GetMessage(shortMessage.Id, optionalClient);
                        }

                        TelegramEventBubble(new TypingBubble(Time.GetNowUnixTimestamp(),
                            Bubble.BubbleDirection.Incoming,
                            fromId, false, this, false, false));
                        TextBubble textBubble = new TextBubble(
                                            useCurrentTime ? Time.GetNowUnixTimestamp() : (long)shortMessage.Date,
                                            shortMessage.Out != null ? Bubble.BubbleDirection.Outgoing : Bubble.BubbleDirection.Incoming,
                                            fromId, null, false, this, shortMessage.Message,
                                            shortMessage.Id.ToString(CultureInfo.InvariantCulture));

                        if (shortMessage.Out != null)
                        {
                            textBubble.Status = Bubble.BubbleStatus.Sent;
							BubbleGroupManager.SetUnread(this, false, fromId);
							NotificationManager.Remove(this, fromId);
                        }
                        if (textBubble.Direction == Bubble.BubbleDirection.Incoming)
                        {
                            if (shortMessage.ReplyToMsgId != 0)
                            {
                                var iReplyMessage = GetMessage(shortMessage.ReplyToMsgId, optionalClient);
                                DebugPrint(">>> got message " + ObjectDumper.Dump(iReplyMessage));
                                var replyMessage = iReplyMessage as Message;
                                AddQuotedMessageToBubble(replyMessage, textBubble);

                            }
                        }
                        TelegramEventBubble(textBubble);
                    }
                    if (shortMessage.Id > maxMessageId)
                    {
                        maxMessageId = shortMessage.Id;
                    }
                }
                else if (updateUserPhoto != null)
                {
                    var iUpdatedUser = _dialogs.GetUser(updateUserPhoto.UserId);
                    var updatedUser = iUpdatedUser as User;
                    if (updatedUser != null)
                    {
                        updatedUser.Photo = updateUserPhoto.Photo;
                        InvalidateThumbnail(updatedUser.Id.ToString(), false, false);
                        InvalidateThumbnail(updatedUser.Id.ToString(), false, true);
                    }
                    _dialogs.AddUser(updatedUser);
                }
                else if (updateChannel != null)
                {
                    var channel = _dialogs.GetChat(updateChannel.ChannelId) as Channel;
                    if (channel != null)
                    {
                        if (channel.Left != null)
                        { 
                            var bubbleGroup = BubbleGroupManager.FindWithAddress(this, updateChannel.ChannelId.ToString());
                            Platform.DeleteBubbleGroup(new BubbleGroup[] { bubbleGroup });
                        }
                    }
                }
                else if (updateChatAdmins != null)
                {
                    var chatToUpdate = _dialogs.GetUser(updateChatAdmins.ChatId) as Chat;
                    if (chatToUpdate != null)
                    {
                        if (updateChatAdmins.Enabled)
                        {
                            chatToUpdate.AdminsEnabled = new True();
                        }
                        else
                        {
                            chatToUpdate.AdminsEnabled = null;
                        }
                    }
                    _dialogs.AddChat(chatToUpdate);
                }
                else if (updateReadHistoryOutbox != null)
                {
                    var iPeer = updateReadHistoryOutbox.Peer;
                    var peerChat = iPeer as PeerChat;
                    var peerUser = iPeer as PeerUser;

                    if (peerUser != null)
                    {
                        BubbleGroup bubbleGroup = BubbleGroupManager.FindWithAddress(this,
                            peerUser.UserId.ToString(CultureInfo.InvariantCulture));
                        DebugPrint("Found bubble group " + bubbleGroup);
                        if (bubbleGroup != null)
                        {
                            string idString = bubbleGroup.LastBubbleSafe().IdService;
                            if (idString == updateReadHistoryOutbox.MaxId.ToString(CultureInfo.InvariantCulture))
                            {
                                TelegramEventBubble(
                                    new ReadBubble(Time.GetNowUnixTimestamp(),
                                        Bubble.BubbleDirection.Incoming, this,
                                        peerUser.UserId.ToString(CultureInfo.InvariantCulture), null,
                                        Time.GetNowUnixTimestamp(), false, false));
                            }
                        }
                    }
                    else if (peerChat != null)
                    {
                        BubbleGroup bubbleGroup = BubbleGroupManager.FindWithAddress(this,
                           peerChat.ChatId.ToString(CultureInfo.InvariantCulture));
                        if (bubbleGroup != null)
                        {
                            string idString = bubbleGroup.LastBubbleSafe().IdService;
                            if (idString == updateReadHistoryOutbox.MaxId.ToString(CultureInfo.InvariantCulture))
                            {
                                TelegramEventBubble(
                                    new ReadBubble(
                                        Time.GetNowUnixTimestamp(),
                                        Bubble.BubbleDirection.Incoming, this,
                                        peerChat.ChatId.ToString(CultureInfo.InvariantCulture), DisaReadTime.SingletonPartyParticipantAddress,
                                        Time.GetNowUnixTimestamp(), true, false));
                            }
                        }

                    }

                }
                else if (updateReadChannelOutbox != null)
                {
                    BubbleGroup bubbleGroup = BubbleGroupManager.FindWithAddress(this,
                          updateReadChannelOutbox.ChannelId.ToString(CultureInfo.InvariantCulture));
                    if (bubbleGroup != null)
                    {
                        string idString = bubbleGroup.LastBubbleSafe().IdService;
                        if (idString == updateReadChannelOutbox.MaxId.ToString(CultureInfo.InvariantCulture))
                        {
                            TelegramEventBubble(
                                new ReadBubble(
                                    Time.GetNowUnixTimestamp(),
                                    Bubble.BubbleDirection.Incoming, this,
                                    updateReadChannelOutbox.ChannelId.ToString(CultureInfo.InvariantCulture), DisaReadTime.SingletonPartyParticipantAddress,
                                    Time.GetNowUnixTimestamp(), true, false));
                        }
                    }
                }
                else if (updateReadHistoryInbox != null)
                {
                    DebugPrint(">>> In update read history inbox");
                    var iPeer = updateReadHistoryInbox.Peer;
                    var peerChat = iPeer as PeerChat;
                    var peerUser = iPeer as PeerUser;

                    if (peerUser != null)
                    {
                        BubbleGroup bubbleGroup = BubbleGroupManager.FindWithAddress(this,
                            peerUser.UserId.ToString(CultureInfo.InvariantCulture));

                        if (bubbleGroup == null)
                        {
                            continue;
                        }

                        string idString = bubbleGroup.LastBubbleSafe().IdService;
                        DebugPrint("idstring" + idString);
						if (idString != null)
						{
							if (uint.Parse(idString) <= updateReadHistoryInbox.MaxId)
							{
								BubbleGroupManager.SetUnread(this, false, peerUser.UserId.ToString(CultureInfo.InvariantCulture));
								NotificationManager.Remove(this, peerUser.UserId.ToString(CultureInfo.InvariantCulture));
							}
						}
                    }
                    else if (peerChat != null)
                    {
                        BubbleGroup bubbleGroup = BubbleGroupManager.FindWithAddress(this,
                            peerChat.ChatId.ToString(CultureInfo.InvariantCulture));
                        if (bubbleGroup == null)
                        {
                            continue;
                        }
                        string idString = bubbleGroup.LastBubbleSafe().IdService;
                        if (idString != null)
                        {
                            if (uint.Parse(idString) == updateReadHistoryInbox.MaxId)
                            {
                                BubbleGroupManager.SetUnread(this, false,
                                    peerChat.ChatId.ToString(CultureInfo.InvariantCulture));
                                NotificationManager.Remove(this, peerChat.ChatId.ToString(CultureInfo.InvariantCulture));
                            }
                        }

                    }

                }
                else if (updateReadChannelInbox != null)
                {
                    BubbleGroup bubbleGroup = BubbleGroupManager.FindWithAddress(this,
                                              updateReadChannelInbox.ChannelId.ToString(CultureInfo.InvariantCulture));
                    if (bubbleGroup == null)
                    {
                        continue;
                    }

                    string idString = bubbleGroup.LastBubbleSafe().IdService;
                    DebugPrint("idstring" + idString);
                    if (idString != null)
                    {
                        if (uint.Parse(idString) <= updateReadChannelInbox.MaxId)
                        {
                            BubbleGroupManager.SetUnread(this, false, updateReadChannelInbox.ChannelId.ToString(CultureInfo.InvariantCulture));
                            NotificationManager.Remove(this, updateReadChannelInbox.ChannelId.ToString(CultureInfo.InvariantCulture));
                        }
                    }
                }
                else if (shortChatMessage != null)
                {
                    if (!string.IsNullOrWhiteSpace(shortChatMessage.Message))
                    {
                        var address = shortChatMessage.ChatId.ToString(CultureInfo.InvariantCulture);
                        var participantAddress = shortChatMessage.FromId.ToString(CultureInfo.InvariantCulture);
                        TelegramEventBubble(new TypingBubble(Time.GetNowUnixTimestamp(),
                            Bubble.BubbleDirection.Incoming,
                            address, participantAddress, true, this, false, false));
						var shortMessageChat = _dialogs.GetChat(shortChatMessage.ChatId);
						if (shortMessageChat == null)
						{
							DebugPrint(">>>>> Chat is null, fetching user from the server");
							GetMessage(shortChatMessage.Id, optionalClient);
						}
                        TextBubble textBubble = new TextBubble(
                            useCurrentTime ? Time.GetNowUnixTimestamp() : (long)shortChatMessage.Date,
                            shortChatMessage.Out != null
                                ? Bubble.BubbleDirection.Outgoing
                                : Bubble.BubbleDirection.Incoming,
                            address, participantAddress, true, this, shortChatMessage.Message,
                            shortChatMessage.Id.ToString(CultureInfo.InvariantCulture));
                        if (shortChatMessage.Out != null)
                        {
                            textBubble.Status = Bubble.BubbleStatus.Sent;
							BubbleGroupManager.SetUnread(this, false, address);
							NotificationManager.Remove(this, address);
                        }
                        if (textBubble.Direction == Bubble.BubbleDirection.Incoming)
                        {
                            if (shortChatMessage.ReplyToMsgId != 0)
                            {
                                var iReplyMessage = GetMessage(shortChatMessage.ReplyToMsgId, optionalClient);
                                DebugPrint(">>> got message " + ObjectDumper.Dump(iReplyMessage));
                                var replyMessage = iReplyMessage as Message;
                                AddQuotedMessageToBubble(replyMessage, textBubble);
                            }
                        }
                        TelegramEventBubble(textBubble);
                    }
                    if (shortChatMessage.Id > maxMessageId)
                    {
                        maxMessageId = shortChatMessage.Id;
                    }
                }
                else if (message != null)
                {
                    var bubbles = ProcessFullMessage(message, useCurrentTime);
                    var i = 0;
                    foreach (var bubble in bubbles)
                    {
                        if (bubble != null)
                        {
							if (bubble.Direction == Bubble.BubbleDirection.Incoming)
							{
								var fromId = message.FromId.ToString(CultureInfo.InvariantCulture);
								var messageUser = _dialogs.GetUser(message.FromId);
								if (messageUser == null)
								{
									DebugPrint(">>>>> User is null, fetching user from the server");
									GetMessage(message.Id, optionalClient, uint.Parse(TelegramUtils.GetPeerId(message.ToId)), message.ToId is PeerChannel);
								}
								if (message.ReplyToMsgId != 0 && i == 0)//we should only add quoted message to first bubble if multiple bubbles exist
								{
									var iReplyMessage = GetMessage(message.ReplyToMsgId, optionalClient, uint.Parse(TelegramUtils.GetPeerId(message.ToId)), message.ToId is PeerChannel);
									DebugPrint(">>> got message " + ObjectDumper.Dump(iReplyMessage));
									var replyMessage = iReplyMessage as Message;
									AddQuotedMessageToBubble(replyMessage, bubble);
								}
							}
							else if (bubble.Direction == Bubble.BubbleDirection.Outgoing)
							{
								BubbleGroupManager.SetUnread(this, false, bubble.Address);
								NotificationManager.Remove(this, bubble.Address);
							}
                            TelegramEventBubble(bubble);
                        }
                        i++;
                    }
                    if (message.Id > maxMessageId)
                    {
                        maxMessageId = message.Id;
                    }
                }
                else if (updateContactRegistered != null)
                {
                    contactsCache = new List<User>(); //invalidate cache
                }
                else if (updateContactLink != null)
                {
                    contactsCache = new List<User>();
                }
                else if (userStatus != null)
                {
                    var available = TelegramUtils.GetAvailable(userStatus.Status);
                    var userToUpdate = _dialogs.GetUser(userStatus.UserId);
                    if (userToUpdate != null)
                    {
                        var userToUpdateAsUser = userToUpdate as User;
                        if (userToUpdateAsUser != null)
                        {
                            userToUpdateAsUser.Status = userStatus.Status;
                            _dialogs.AddUser(userToUpdateAsUser);
                        }
                    }

                    TelegramEventBubble(new PresenceBubble(Time.GetNowUnixTimestamp(),
                        Bubble.BubbleDirection.Incoming,
                        userStatus.UserId.ToString(CultureInfo.InvariantCulture),
                        false, this, available));
                }
                else if (typing != null || typingChat != null)
                {
                    var isAudio = false;
                    var isTyping = false;
                    if (typing != null)
                    {
                        isAudio = typing.Action is SendMessageRecordAudioAction;
                        isTyping = typing.Action is SendMessageTypingAction;
                    }
                    if (typingChat != null)
                    {
                        isAudio = typingChat.Action is SendMessageRecordAudioAction;
                        isTyping = typingChat.Action is SendMessageTypingAction;
                    }
                    var userId = typing != null ? typing.UserId : typingChat.UserId;
                    var party = typingChat != null;
                    var participantAddress = party ? userId.ToString(CultureInfo.InvariantCulture) : null;
                    var address = party
                        ? typingChat.ChatId.ToString(CultureInfo.InvariantCulture)
                        : userId.ToString(CultureInfo.InvariantCulture);
                    var key = address + participantAddress;


                    if (isAudio || isTyping)
                    {
                        TelegramEventBubble(new TypingBubble(Time.GetNowUnixTimestamp(),
                            Bubble.BubbleDirection.Incoming,
                            address, participantAddress, party,
                            this, true, isAudio));
                        CancelTypingTimer(key);
                        var newTimer = new Timer(6000) { AutoReset = false };
                        newTimer.Elapsed += (sender2, e2) =>
                        {
                            EventBubble(new TypingBubble(Time.GetNowUnixTimestamp(),
                                Bubble.BubbleDirection.Incoming,
                                address, participantAddress, party,
                                this, false, isAudio));
                            newTimer.Dispose();
                            _typingTimers.Remove(key);
                        };
                        _typingTimers[key] = newTimer;
                        newTimer.Start();
                    }
                    else
                    {
                        //Console.WriteLine("Unknown typing action: " + typing.Action.GetType().Name); //causes null pointer in some cases
                    }
                }
                else if (user != null)
                {
                    _dialogs.AddUser(user);
                }
                else if (chat != null)
                {
                    _dialogs.AddChat(chat);
                }
                else if (updateChatParticipants != null)
                {
                    //do nothing, we just use party options for this
                }
                else if (messageService != null)
                {
					var partyInformationBubbles = MakePartyInformationBubble(messageService, useCurrentTime, optionalClient);
                    foreach (var partyInformationBubble in partyInformationBubbles)
                    {
                        TelegramEventBubble(partyInformationBubble);
                    }
                }
                else if (updateChannelTooLong != null)
                {
                    //this dude gives me a pts of 0, which messes up shit. So we wont do nothin mah man
                    //SaveChannelState(updateChannelTooLong.ChannelId, updateChannelTooLong.Pts);
                }
                else if (updateEditChannelMessage != null)
                {
                    var editChannelMessage = updateEditChannelMessage.Message as Message;
                    if (message == null)
                    {
                        continue;
                    }
                    var peerChannel = editChannelMessage.ToId as PeerChannel;
                    SaveChannelState(peerChannel.ChannelId, updateEditChannelMessage.Pts);
                }
                else if (updateDeleteChannelMessage != null)
                {
                    SaveChannelState(updateDeleteChannelMessage.ChannelId, updateDeleteChannelMessage.Pts);
                }
                else
                {
                    Console.WriteLine("Unknown update: " + ObjectDumper.Dump(update));
                }
            }

            //if (maxMessageId != 0)
            //{
            //    SendReceivedMessages(optionalClient, maxMessageId);
            //}
        }

		private List<VisualBubble> MakePartyInformationBubble(MessageService messageService, bool useCurrentTime, TelegramClient optionalClient)
	    {
            var editTitle = messageService.Action as MessageActionChatEditTitle;
            var deleteUser = messageService.Action as MessageActionChatDeleteUser;
            var addUser = messageService.Action as MessageActionChatAddUser;
            var created = messageService.Action as MessageActionChatCreate;
            var thumbnailChanged = messageService.Action as MessageActionChatEditPhoto;
            var upgradedToSuperGroup = messageService.Action as MessageActionChannelMigrateFrom;
            var chatMigaratedToSuperGroup = messageService.Action as MessageActionChatMigrateTo;
            var chatJoinedByInviteLink = messageService.Action as MessageActionChatJoinedByLink;

            var address = TelegramUtils.GetPeerId(messageService.ToId);
            var fromId = messageService.FromId.ToString(CultureInfo.InvariantCulture);

            if (editTitle != null)
            {
                var newTitle = editTitle.Title;
                var chatToUpdate = _dialogs.GetChat(uint.Parse(address));
                if (chatToUpdate != null)
                {
                    TelegramUtils.SetChatTitle(chatToUpdate, newTitle);
                }
                var bubble = PartyInformationBubble.CreateTitleChanged(
                    useCurrentTime ? Time.GetNowUnixTimestamp() : (long)messageService.Date, address,
                    this, messageService.Id.ToString(CultureInfo.InvariantCulture), fromId, newTitle);
                if (messageService.ToId is PeerChannel)
                {
                    bubble.ExtendedParty = true;
                }
                BubbleGroupUpdater.Update(this, address);
                return new List<VisualBubble>
                {
                    bubble
                };
            }
            else if (thumbnailChanged != null)
            {
                InvalidateThumbnail(address, true, true);
                InvalidateThumbnail(address, true, false);
                var bubble = PartyInformationBubble.CreateThumbnailChanged(
                    useCurrentTime ? Time.GetNowUnixTimestamp() : (long)messageService.Date, address,
                    this, messageService.Id.ToString(), null);
                return new List<VisualBubble>
                {
                    bubble
                };
            }
            else if (deleteUser != null)
            {
                var userDeleted = deleteUser.UserId.ToString(CultureInfo.InvariantCulture);
                var bubble = PartyInformationBubble.CreateParticipantRemoved(
                    useCurrentTime ? Time.GetNowUnixTimestamp() : (long)messageService.Date, address,
                    this, messageService.Id.ToString(CultureInfo.InvariantCulture), fromId, userDeleted);
                if (messageService.ToId is PeerChannel)
                {
                    bubble.ExtendedParty = true;
                }
                return new List<VisualBubble>
                {
                    bubble
                };
            }
            else if (addUser != null)
            {
                var returnList = new List<VisualBubble>();
                foreach (var userId in addUser.Users)
                {
                    var userAdded = userId.ToString(CultureInfo.InvariantCulture);
                    var bubble = PartyInformationBubble.CreateParticipantAdded(
                        useCurrentTime ? Time.GetNowUnixTimestamp() : (long)messageService.Date, address,
                        this, messageService.Id.ToString(CultureInfo.InvariantCulture), fromId, userAdded);
                    if (messageService.ToId is PeerChannel)
                    {
                        bubble.ExtendedParty = true;
                    }
                    returnList.Add(bubble);
                }
                return returnList;
            }
            else if (created != null)
            {
                var bubble = PartyInformationBubble.CreateParticipantAdded(
                    useCurrentTime ? Time.GetNowUnixTimestamp() : (long)messageService.Date, address,
                    this, messageService.Id.ToString(CultureInfo.InvariantCulture), fromId,
                    _settings.AccountId.ToString(CultureInfo.InvariantCulture));
				
                if (messageService.ToId is PeerChannel)
                {
                    bubble.ExtendedParty = true;
                }
				var shortMessageChat = _dialogs.GetChat(uint.Parse(address));
				if (shortMessageChat == null)
				{
					DebugPrint(">>>>> Chat is null, fetching user from the server");
					GetMessage(messageService.Id, optionalClient);
				}
                return new List<VisualBubble>
                {
                    bubble
                };
            }
            else if (chatJoinedByInviteLink != null)
            { 
                var bubble = PartyInformationBubble.CreateParticipantAdded(
                    useCurrentTime ? Time.GetNowUnixTimestamp() : (long)messageService.Date, address,
                    this, messageService.Id.ToString(CultureInfo.InvariantCulture), fromId,
                    _settings.AccountId.ToString(CultureInfo.InvariantCulture));
                if (messageService.ToId is PeerChannel)
                {
                    bubble.ExtendedParty = true;
                }
                return new List<VisualBubble>
                {
                    bubble
                };
            }
            else if (upgradedToSuperGroup != null)
            {
                var bubble = PartyInformationBubble.CreateConvertedToExtendedParty(
                    useCurrentTime ? Time.GetNowUnixTimestamp() : (long)messageService.Date, address, this,
                    messageService.Id.ToString(CultureInfo.InvariantCulture));
                bubble.ExtendedParty = true;
                return new List<VisualBubble>
                {
                    bubble
                };
            }
            else if (chatMigaratedToSuperGroup != null)
            {
                if (!_oldMessages)
                {
                    var bubbleGroupToSwitch = BubbleGroupManager.FindWithAddress(this, chatMigaratedToSuperGroup.ChannelId.ToString());
                    var bubbleGroupToDelete = BubbleGroupManager.FindWithAddress(this, address);
                    if (bubbleGroupToSwitch != null)
                    {
						if (Platform.GetCurrentBubbleGroupOnUI() == bubbleGroupToDelete)
						{
							Platform.SwitchCurrentBubbleGroupOnUI(bubbleGroupToSwitch);
						}
                    }
                    if (bubbleGroupToDelete != null)
                    {
                        Platform.DeleteBubbleGroup(new BubbleGroup[] { bubbleGroupToDelete });
                    }
                }
                return new List<VisualBubble>();
            }
            else
            {
                Console.WriteLine("Unknown message service: " + ObjectDumper.Dump(messageService));
                return new List<VisualBubble>();
            }
        }

        private void AddQuotedMessageToBubble(Message replyMessage, VisualBubble bubble)
        {
            if (replyMessage == null)
            {
                return;
            }

            if(!string.IsNullOrEmpty(replyMessage.MessageProperty))
            {
                bubble.QuotedType = VisualBubble.MediaType.Text;
                bubble.QuotedContext = replyMessage.MessageProperty;

            }
            else
            {
                var messageMedia = replyMessage.Media;
                var messageMediaPhoto = messageMedia as MessageMediaPhoto;
                var messageMediaDocument = messageMedia as MessageMediaDocument;
                var messageMediaGeo = messageMedia as MessageMediaGeo;
                var messageMediaVenue = messageMedia  as MessageMediaVenue;
                var messageMediaContact = messageMedia as MessageMediaContact;

                if (messageMediaPhoto != null)
                {
                    bubble.QuotedType = VisualBubble.MediaType.Image;
                    bubble.HasQuotedThumbnail = true;
                    bubble.QuotedThumbnail = GetCachedPhotoBytes(messageMediaPhoto.Photo);
                }
                if(messageMediaDocument != null)
                {
                    var document = messageMediaDocument.Document as Document;
                    if(document!=null)
                    {
                        if (document.MimeType.Contains("audio"))
                        {
                            bubble.QuotedType = VisualBubble.MediaType.Audio;
                            bubble.QuotedSeconds = GetAudioTime(document);
                        }
                        else if (document.MimeType.Contains("video"))
                        {
                            bubble.QuotedType = VisualBubble.MediaType.File;
                            bubble.QuotedContext = "Video";
                        }
                        else
                        {
                            bubble.QuotedType = VisualBubble.MediaType.File;
                            bubble.QuotedContext = GetDocumentFileName(document);
                        }

                    }
                }
                if(messageMediaGeo != null)
                {
                    var geoPoint = messageMediaGeo.Geo as GeoPoint;

                    if (geoPoint != null)
                    {
                        bubble.QuotedType = VisualBubble.MediaType.Location;
                        bubble.QuotedContext = (int)geoPoint.Lat + "," + (int)geoPoint.Long;
                        bubble.QuotedLocationLatitude = geoPoint.Lat;
                        bubble.QuotedLocationLongitude = geoPoint.Long;
                        bubble.HasQuotedThumbnail = true;
                    }
                        
                }
                if(messageMediaVenue != null)
                {
                    var geoPoint = messageMediaVenue.Geo as GeoPoint;

                    if (geoPoint != null)
                    {
                        bubble.QuotedType = VisualBubble.MediaType.Location;
                        bubble.QuotedContext = messageMediaVenue.Title;
                        bubble.QuotedLocationLatitude = geoPoint.Lat;
                        bubble.QuotedLocationLongitude = geoPoint.Long;
                        bubble.HasQuotedThumbnail = true;
                    }

                }
                if(messageMediaContact != null)
                {
                    bubble.QuotedType = VisualBubble.MediaType.Contact;
                    bubble.QuotedContext = messageMediaContact.FirstName + " " + messageMediaContact.LastName; 
                }
                    
            }
            bubble.QuotedAddress = replyMessage.FromId.ToString(CultureInfo.InvariantCulture);
            bubble.QuotedIdService = replyMessage.Id.ToString(CultureInfo.InvariantCulture);

        }

        private List<VisualBubble> ProcessFullMessage(Message message,bool useCurrentTime)
        {
            var peerUser = message.ToId as PeerUser;
            var peerChat = message.ToId as PeerChat;
            var peerChannel = message.ToId as PeerChannel;

            var direction = message.FromId == _settings.AccountId
                ? Bubble.BubbleDirection.Outgoing
                : Bubble.BubbleDirection.Incoming;

            if (!string.IsNullOrWhiteSpace(message.MessageProperty))
            {
                TextBubble tb = null;

                if (peerUser != null)
                {
                    var address = direction == Bubble.BubbleDirection.Incoming
                        ? message.FromId
                        : peerUser.UserId;
                    var addressStr = address.ToString(CultureInfo.InvariantCulture);
                    tb = new TextBubble(
                        useCurrentTime ? Time.GetNowUnixTimestamp() : (long)message.Date,
                        direction, addressStr, null, false, this, message.MessageProperty,
                        message.Id.ToString(CultureInfo.InvariantCulture));

                }
                else if (peerChat != null)
                {
                    var address = peerChat.ChatId.ToString(CultureInfo.InvariantCulture);
                    var participantAddress = message.FromId.ToString(CultureInfo.InvariantCulture);
                    tb = new TextBubble(
                        useCurrentTime ? Time.GetNowUnixTimestamp() : (long)message.Date,
                        direction, address, participantAddress, true, this, message.MessageProperty,
                        message.Id.ToString(CultureInfo.InvariantCulture));
                }
                else if (peerChannel != null) 
                {
                    var address = peerChannel.ChannelId.ToString(CultureInfo.InvariantCulture);
                    var participantAddress = message.FromId.ToString(CultureInfo.InvariantCulture);
                    tb = new TextBubble(
                        useCurrentTime ? Time.GetNowUnixTimestamp() : (long)message.Date,
                        direction, address, participantAddress, true, this, message.MessageProperty,
                        message.Id.ToString(CultureInfo.InvariantCulture));
                    tb.ExtendedParty = true;
                }
                if (tb == null) return null;
                if (direction == Bubble.BubbleDirection.Outgoing)
                {
                    tb.Status = Bubble.BubbleStatus.Sent;
                }
                return new List<VisualBubble> 
                { 
                    tb
                };
            }
            else
            {
                if (peerUser != null)
                {
                    var address = direction == Bubble.BubbleDirection.Incoming
                        ? message.FromId
                        : peerUser.UserId;
                    var addressStr = address.ToString(CultureInfo.InvariantCulture);
                    var bubble = MakeMediaBubble(message, useCurrentTime, true, addressStr);
                    return bubble;
                }
                else if (peerChat != null)
                {
                    var address = peerChat.ChatId.ToString(CultureInfo.InvariantCulture);
                    var participantAddress = message.FromId.ToString(CultureInfo.InvariantCulture);
                    var bubble = MakeMediaBubble(message, useCurrentTime, false, address, participantAddress);
                    return bubble;
                }
                else if (peerChannel != null)
                {
                    var address = peerChannel.ChannelId.ToString(CultureInfo.InvariantCulture);
                    var participantAddress = message.FromId.ToString(CultureInfo.InvariantCulture);
                    var bubbles = MakeMediaBubble(message, useCurrentTime, false, address, participantAddress);
                    foreach (var bubble in bubbles)
                    {
                        bubble.ExtendedParty = true;   
                    }
                    return bubbles;
                }

            }
            return null;
        }

        private IMessagesMessages FetchMessage(uint id, TelegramClient client, uint toId, bool superGroup)
        {
            if (superGroup)
            {
                try
                {
                    var result = TelegramUtils.RunSynchronously(client.Methods.ChannelsGetMessagesAsync(new ChannelsGetMessagesArgs
                    {
                        Channel = new InputChannel
                        {
                            ChannelId = toId,
                            AccessHash = TelegramUtils.GetChannelAccessHash(_dialogs.GetChat(toId))
                        },
                        Id = new List<uint>
                        {
                            id
                        }
                    }));
                    return result;
                }
                catch (Exception e)
                {
                    DebugPrint("Exeption while getting channel message" +  e);
                    return MakeMessagesMessages(new List<IChat>(), new List<IUser>(), new List<IMessage>());
                }
            }
            else
            {
                return TelegramUtils.RunSynchronously(client.Methods.MessagesGetMessagesAsync(new MessagesGetMessagesArgs
                {
                    Id = new List<uint>
                    {
                        id
                    }
                }));
            }
        }

        private IMessage GetMessage(uint messageId, TelegramClient optionalClient, uint senderId = 0, bool superGroup = false)
        {
            Func<TelegramClient,IMessage> fetch = client =>
            {
                var messagesmessages = FetchMessage(messageId, client, senderId, superGroup);
                var messages = TelegramUtils.GetMessagesFromMessagesMessages(messagesmessages);
                var chats = TelegramUtils.GetChatsFromMessagesMessages(messagesmessages);
                var users = TelegramUtils.GetUsersFromMessagesMessages(messagesmessages);

                _dialogs.AddUsers(users);
                _dialogs.AddChats(chats);

                if (messages != null && messages.Count > 0)
                {
                    return messages[0];
                }
                return null;
            };

            if (optionalClient != null)
            {
                return fetch(optionalClient);
            }
            else
            {
                using (var client = new FullClientDisposable(this))
                {
                    return fetch(client.Client);
                }
            }  
        }

        private List<VisualBubble> MakeMediaBubble(Message message, bool useCurrentTime, bool isUser, string addressStr, string participantAddress = null)
        {
            DebugPrint(">>>>>>> Making media bubble");
            var messageMedia = message.Media;
            var messageMediaPhoto = messageMedia as MessageMediaPhoto;
            var messageMediaDocument = messageMedia as MessageMediaDocument;
            var messageMediaGeo = messageMedia as MessageMediaGeo;
            var messageMediaVenue = messageMedia as MessageMediaVenue;
            var messageMediaContact = messageMedia as MessageMediaContact;

            if (messageMediaPhoto != null)
            {
                var fileLocation = GetPhotoFileLocation(messageMediaPhoto.Photo);
                var fileSize = GetPhotoFileSize(messageMediaPhoto.Photo);
                var dimensions = GetPhotoDimensions(messageMediaPhoto.Photo);
                var cachedPhoto = GetCachedPhotoBytes(messageMediaPhoto.Photo);
                FileInformation fileInfo = new FileInformation
                {
                    FileLocation = fileLocation,
                    Size = fileSize,
                    FileType = "image",
                    Document = new Document()
                };
                using (var memoryStream = new MemoryStream())
                {
                    Serializer.Serialize<FileInformation>(memoryStream, fileInfo);
                    ImageBubble imageBubble = null;
                    if (isUser)
                    {
                        imageBubble = new ImageBubble(useCurrentTime ? Time.GetNowUnixTimestamp() : (long) message.Date,
                            message.Out != null ? Bubble.BubbleDirection.Outgoing : Bubble.BubbleDirection.Incoming,
                            addressStr, null, false, this, null, ImageBubble.Type.Url,
                            cachedPhoto, message.Id.ToString(CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        imageBubble = new ImageBubble(useCurrentTime ? Time.GetNowUnixTimestamp() : (long) message.Date,
                            message.Out != null ? Bubble.BubbleDirection.Outgoing : Bubble.BubbleDirection.Incoming,
                            addressStr, participantAddress, true, this, null,
                            ImageBubble.Type.Url, cachedPhoto, message.Id.ToString(CultureInfo.InvariantCulture));
                    }
                    if (imageBubble.Direction == Bubble.BubbleDirection.Outgoing)
                    {
                        imageBubble.Status = Bubble.BubbleStatus.Sent;
                    }
                    imageBubble.AdditionalData = memoryStream.ToArray();
                    imageBubble.Width = dimensions.Width;
                    imageBubble.Height = dimensions.Height;
                    var returnList = new List<VisualBubble>
                    {
                        imageBubble  
                    };
                    if (!string.IsNullOrEmpty(messageMediaPhoto.Caption))
                    {
                        TextBubble captionBubble = null;
                        if (isUser)
                        {
                            captionBubble = new TextBubble(useCurrentTime ? Time.GetNowUnixTimestamp() : (long)message.Date,
                                message.Out != null ? Bubble.BubbleDirection.Outgoing : Bubble.BubbleDirection.Incoming,
                                addressStr, null, false, this, messageMediaPhoto.Caption,
                                message.Id.ToString(CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            captionBubble = new TextBubble(useCurrentTime ? Time.GetNowUnixTimestamp() : (long)message.Date,
                                message.Out != null ? Bubble.BubbleDirection.Outgoing : Bubble.BubbleDirection.Incoming,
                                addressStr, participantAddress, true, this, messageMediaPhoto.Caption,
                                message.Id.ToString(CultureInfo.InvariantCulture));
                        }
                        returnList.Add(captionBubble);
                    }
                    return returnList;
                }
                
            }
            else if (messageMediaDocument != null)
            {
                DebugPrint(">>>> Media document " + ObjectDumper.Dump(messageMediaDocument));
                //DebugPrint(">>>>> Media attributes " +  (messageMediaDocument.Document as Document).Attributes);
                var document = messageMediaDocument.Document as Document;
                if (document != null)
                {
                    FileInformation fileInfo = new FileInformation
                    {
                        FileType = "document",
                        Document = document
                    };
                    using (var memoryStream = new MemoryStream())
                    {
                        Serializer.Serialize<FileInformation>(memoryStream, fileInfo);
                        VisualBubble bubble = null;
                        if (document.MimeType.Contains("audio"))
                        {
                            var audioTime = (int) GetAudioTime(document);
                            if (isUser)
                            {
                                bubble =
                                    new AudioBubble(useCurrentTime ? Time.GetNowUnixTimestamp() : (long) message.Date,
                                        message.Out != null
                                            ? Bubble.BubbleDirection.Outgoing
                                            : Bubble.BubbleDirection.Incoming, addressStr, null, false, this, "",
                                        AudioBubble.Type.Url,
                                        false, audioTime, message.Id.ToString(CultureInfo.InvariantCulture));
                            }
                            else
                            {
                                bubble =
                                    new AudioBubble(useCurrentTime ? Time.GetNowUnixTimestamp() : (long) message.Date,
                                        message.Out != null
                                            ? Bubble.BubbleDirection.Outgoing
                                            : Bubble.BubbleDirection.Incoming, addressStr, participantAddress, true,
                                        this, "",
                                        AudioBubble.Type.Url, false, audioTime,
                                        message.Id.ToString(CultureInfo.InvariantCulture));
                            }
                        }
                        else
                        {
                            //TODO: localize
                            var filename = document.MimeType.Contains("video")
                                ? ""
                                : GetDocumentFileName(document);GetDocumentFileName(document);

                            if (isUser)
                            {
                                bubble =
                                    new FileBubble(useCurrentTime ? Time.GetNowUnixTimestamp() : (long) message.Date,
                                        message.Out != null
                                            ? Bubble.BubbleDirection.Outgoing
                                            : Bubble.BubbleDirection.Incoming, addressStr, null, false, this, "",
                                        FileBubble.Type.Url, filename, document.MimeType,
                                        message.Id.ToString(CultureInfo.InvariantCulture));
                            }
                            else
                            {
                                bubble =
                                    new FileBubble(useCurrentTime ? Time.GetNowUnixTimestamp() : (long) message.Date,
                                        message.Out != null
                                            ? Bubble.BubbleDirection.Outgoing
                                            : Bubble.BubbleDirection.Incoming, addressStr, participantAddress, true,
                                        this, "", FileBubble.Type.Url, filename, document.MimeType,
                                        message.Id.ToString(CultureInfo.InvariantCulture));
                            }

                        }

                        if (bubble.Direction == Bubble.BubbleDirection.Outgoing)
                        {
                            bubble.Status = Bubble.BubbleStatus.Sent;
                        }
                        bubble.AdditionalData = memoryStream.ToArray();

                        var returnList = new List<VisualBubble>
                        {
                            bubble
                        };
                        if (!string.IsNullOrEmpty(messageMediaDocument.Caption))
                        {
                            TextBubble captionBubble = null;
                            if (isUser)
                            {
                                captionBubble = new TextBubble(useCurrentTime ? Time.GetNowUnixTimestamp() : (long)message.Date,
                                    message.Out != null ? Bubble.BubbleDirection.Outgoing : Bubble.BubbleDirection.Incoming,
                                    addressStr, null, false, this, messageMediaDocument.Caption,
                                    message.Id.ToString(CultureInfo.InvariantCulture));
                            }
                            else
                            {
                                captionBubble = new TextBubble(useCurrentTime ? Time.GetNowUnixTimestamp() : (long)message.Date,
                                    message.Out != null ? Bubble.BubbleDirection.Outgoing : Bubble.BubbleDirection.Incoming,
                                    addressStr, participantAddress, true, this, messageMediaDocument.Caption,
                                    message.Id.ToString(CultureInfo.InvariantCulture));
                            }
                            returnList.Add(captionBubble);
                        }
                        return returnList;
                    }

                }

            }
            else if (messageMediaGeo != null)
            {

                var geoPoint = messageMediaGeo.Geo as GeoPoint;

                if (geoPoint != null)
                {
                    var geoBubble = MakeGeoBubble(geoPoint, message, isUser, useCurrentTime, addressStr, participantAddress, null);
                    return new List<VisualBubble>
                    {
                        geoBubble
                    };
                }


            }
            else if (messageMediaVenue != null)
            {
                var geoPoint = messageMediaVenue.Geo as GeoPoint;

                if (geoPoint != null)
                {
                    var geoBubble = MakeGeoBubble(geoPoint,message,isUser,useCurrentTime,addressStr,participantAddress,messageMediaVenue.Title);
                    return new List<VisualBubble>
                    {
                        geoBubble
                    };
                }

            }
            else if (messageMediaContact != null)
            {
                var contactCard = new ContactCard
                {
                    GivenName = messageMediaContact.FirstName,
                    FamilyName = messageMediaContact.LastName
                };
                contactCard.Phones.Add(new ContactCard.ContactCardPhone
                {
                    Number = messageMediaContact.PhoneNumber
                });
                var vCardData = Platform.GenerateBytesFromContactCard(contactCard);
                var contactBubble = MakeContactBubble(message, isUser, useCurrentTime, addressStr, participantAddress, vCardData, messageMediaContact.FirstName);

                return new List<VisualBubble>
                { 
                    contactBubble
                };
            }

            return new List<VisualBubble>();
        }

        private ContactBubble MakeContactBubble(Message message, bool isUser, bool useCurrentTime, string addressStr, string participantAddress, byte[] vCardData, string name)
        {
            ContactBubble bubble;

            if (isUser)
            {
                bubble = new ContactBubble(useCurrentTime ? Time.GetNowUnixTimestamp() : (long)message.Date,
                         message.Out != null ? Bubble.BubbleDirection.Outgoing : Bubble.BubbleDirection.Incoming, addressStr,
                         null, false, this, name, vCardData,
                         message.Id.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                bubble = new ContactBubble(useCurrentTime ? Time.GetNowUnixTimestamp() : (long)message.Date,
                         message.Out != null ? Bubble.BubbleDirection.Outgoing : Bubble.BubbleDirection.Incoming, addressStr,
                         participantAddress, true, this, name, vCardData,
                         message.Id.ToString(CultureInfo.InvariantCulture));
            }
            if (bubble.Direction == Bubble.BubbleDirection.Outgoing)
            {
                bubble.Status = Bubble.BubbleStatus.Sent;
            }

            return bubble;
        }

        private VisualBubble MakeGeoBubble(GeoPoint geoPoint,Message message,bool isUser,bool useCurrentTime,string addressStr,string participantAddress,string name)
        {
            LocationBubble bubble;
            if (isUser)
            {
                bubble = new LocationBubble(useCurrentTime ? Time.GetNowUnixTimestamp() : (long)message.Date,
                    message.Out != null ? Bubble.BubbleDirection.Outgoing : Bubble.BubbleDirection.Incoming, addressStr, null, false, this, geoPoint.Long, geoPoint.Lat,
                    "", null, message.Id.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                bubble = new LocationBubble(useCurrentTime ? Time.GetNowUnixTimestamp() : (long)message.Date,
                    message.Out != null ? Bubble.BubbleDirection.Outgoing : Bubble.BubbleDirection.Incoming, addressStr, participantAddress, true, this, geoPoint.Long, geoPoint.Lat,
                    "", null, message.Id.ToString(CultureInfo.InvariantCulture));
            }
            if (name != null) 
            {
                bubble.Name = name;
            }
            if (bubble.Direction == Bubble.BubbleDirection.Outgoing)
            {
                bubble.Status = Bubble.BubbleStatus.Sent;
            }
            return bubble;
        }

        private string GetDocumentFileName(Document document)
        {
            foreach (var attribute in document.Attributes)
            {
                var attributeFilename = attribute as DocumentAttributeFilename;
                if (attributeFilename != null)
                {
                    return attributeFilename.FileName;
                }
            }
            return null;
        }

        private uint GetAudioTime(Document document)
        {
            foreach (var attribute in document.Attributes)
            {
                var attributeAudio = attribute as DocumentAttributeAudio;
                if (attributeAudio != null)
                {
                    return attributeAudio.Duration;
                }
            }
            return 0;
        }

        private uint GetPhotoFileSize(IPhoto iPhoto)
        {
            var photo = iPhoto as Photo;

            if (photo != null)
            {
                foreach (var photoSize in photo.Sizes)
                {
                    var photoSizeNormal = photoSize as PhotoSize;
                    if (photoSizeNormal != null)
                    {
                        if (photoSizeNormal.Type == "x")
                        {
                            return photoSizeNormal.Size;
                        }
                    }

                }
            }
            return 0;
        }

        private Size GetPhotoDimensions(IPhoto iPhoto)
        {
            var photo = iPhoto as Photo;

            if (photo != null)
            {
                // Try and fetch the cached size.
                foreach (var photoSize in photo.Sizes)
                {
                    var photoSizeCached = photoSize as PhotoCachedSize;
                    if (photoSizeCached != null)
                    {
                        return new Size((int)photoSizeCached.W, (int)photoSizeCached.H);
                    }
                }

                // Can't find the cached size? Use the downloaded size.
                foreach (var photoSize in photo.Sizes)
                {
                    var photoSizeNormal = photoSize as PhotoSize;
                    if (photoSizeNormal != null)
                    {
                        if (photoSizeNormal.Type == "x")
                        {
                            return new Size((int)photoSizeNormal.W, (int)photoSizeNormal.H);
                        }
                    }
                }
            }

            return new Size(0, 0);
        }

        private FileLocation GetPhotoFileLocation(IPhoto iPhoto)
        {
            var photo = iPhoto as Photo;

            if (photo != null)
            {
                foreach (var photoSize in photo.Sizes)
                {
                    var photoSizeNormal = photoSize as PhotoSize;
                    if (photoSizeNormal != null)
                    {
                        if (photoSizeNormal.Type == "x")
                        {
                            return photoSizeNormal.Location as FileLocation;
                        }
                    }

                }
            }
            return null;
        }


        private FileLocation GetCachedPhotoFileLocation(IPhoto iPhoto)
        {
            var photo = iPhoto as Photo;

            if (photo != null)
            {
                foreach (var photoSize in photo.Sizes)
                {
                    var photoSizeSmall = photoSize as PhotoSize;
                    if (photoSizeSmall != null)
                    {
                        if (photoSizeSmall.Type == "s")
                        {
                            return photoSizeSmall.Location as FileLocation;
                        }
                    }

                }
            }
            return null;
        }

        private byte[] GetCachedPhotoBytes(IPhoto iPhoto)
        {
            var photo = iPhoto as Photo;

            if (photo != null)
            {
                foreach (var photoSize in photo.Sizes)
                {
                    var photoSizeCached = photoSize as PhotoCachedSize;
                    if (photoSizeCached != null)
                    {
                        return photoSizeCached.Bytes;
                    }

                }
            }
            return null;
        }


        private class OptionalClientDisposable : IDisposable
        {
            private readonly TelegramClient _optionalClient;
            private readonly FullClientDisposable _fullClient;

            public OptionalClientDisposable(Telegram telegram, TelegramClient optionalClient = null)
            {
                _optionalClient = optionalClient;
                if (_optionalClient == null)
                {
                    _fullClient = new FullClientDisposable(telegram);
                }
            }

            public TelegramClient Client
            {
                get
                {
                    if (_optionalClient != null)
                    {
                        return _optionalClient;
                    }
                    else
                    {
                        return _fullClient.Client;
                    }
                }
            }

            public void Dispose()
            {
                if (_fullClient != null)
                {
                    _fullClient.Dispose();
                }
            }
        }

        private void SendReceivedMessages(TelegramClient optionalClient, uint maxId)
        {
            Task.Factory.StartNew(() =>
            {
                using (var disposable = new OptionalClientDisposable(this, optionalClient))
                {
                    var items = TelegramUtils.RunSynchronously(disposable.Client.Methods
                        .MessagesReceivedMessagesAsync(new MessagesReceivedMessagesArgs
                    {
                            MaxId = maxId,
                    }));
                    DebugPrint("items " + ObjectDumper.Dump(items));
                }
            }); 
        }
            
        private void OnLongPollClientClosed(object sender, EventArgs e)
        {
            if (!_longPollerAborted)
            {
                Utils.DebugPrint("Looks like a long poll client closed itself internally. Restarting Telegram...");
                RestartTelegram(null);
            }
        }

        private void OnLongPollClientUpdateTooLong(object sender, EventArgs e)
        {
            if (IsFullClientConnected)
                return;
            Task.Factory.StartNew(() =>
            {
                var transportConfig = 
                    new TcpClientTransportConfig(_settings.NearestDcIp, _settings.NearestDcPort);
                using (var client = new TelegramClient(transportConfig, 
                    new ConnectionConfig(_settings.AuthKey, _settings.Salt), AppInfo))
                {
                    MTProtoConnectResult result = MTProtoConnectResult.Other;
                    try
                    {
                        result = TelegramUtils.RunSynchronously(client.Connect());
                    }
                    catch (Exception ex)
                    {
                        DebugPrint("Exception while connecting " + ex);
                    }
                    if (result != MTProtoConnectResult.Success)
                    {
                        throw new Exception("Failed to connect: " + result);
                    }  
                    FetchState(client);
                }
            });
        }

        private void SendPresence(TelegramClient client)
        {
            TelegramUtils.RunSynchronously(client.Methods.AccountUpdateStatusAsync(
                new AccountUpdateStatusArgs
            {
                Offline = !_hasPresence
            }));
        }

        private void Ping(TelegramClient client, Action<Exception> exception = null)
        {
            try
            {
                Utils.DebugPrint("Sending ping!");
                var pong = (Pong)TelegramUtils.RunSynchronously(client.ProtoMethods.PingAsync(new PingArgs
                {
                    PingId = GetRandomId(),
                }));
                Utils.DebugPrint("Got pong (from ping): " + pong.MsgId);
            }
            catch (Exception ex)
            {
                Utils.DebugPrint("Failed to ping client: " + ex);
                if (exception != null)
                {
                    exception(ex);
                }
            }
        }
            
        private async void PingDelay(TelegramClient client, uint disconnectDelay, Action<Exception> exception = null)
        {
            try
            {
                Utils.DebugPrint(">>>>>>>>> Sending pingDelay!");
                var pong = (Pong)await client.ProtoMethods.PingDelayDisconnectAsync(new PingDelayDisconnectArgs
                {
                    PingId = GetRandomId(),
                    DisconnectDelay = disconnectDelay
                });
                Utils.DebugPrint(">>>>>>>>>> Got pong (from pingDelay): " + pong.MsgId);
            }
            catch (Exception ex)
            {
                if (exception != null)
                {
                    exception(ex);
                }
            }
        }

        private void RestartTelegram(Exception exception)
        {
            if (exception != null)
            {
                Utils.DebugPrint("Restarting Telegram: " + exception);
            }
            else
            {
                Utils.DebugPrint("Restarting Telegram");
            }
            // start a new task, freeing the possibility that there could be a wake lock being held
            Task.Factory.StartNew(() =>
            {
                ServiceManager.Restart(this);
            });
        }

        private void ScheduleLongPollPing()
        {
            RemoveLongPollPingIfPossible();
            _longPollHeartbeart = new WakeLockBalancer.GracefulWakeLock(new WakeLockBalancer.ActionObject(() =>
            {
                if (_longPollClient == null || !_longPollClient.IsConnected)
                {
                    RemoveLongPollPingIfPossible();
                    RestartTelegram(null);
                }
                else
                {
                    Ping(_longPollClient, RestartTelegram);
                }
            }, WakeLockBalancer.ActionObject.ExecuteType.TaskWithWakeLock), 240, 60, true);
            Platform.ScheduleAction(_longPollHeartbeart);
        }

        private void RemoveLongPollPingIfPossible()
        {
            if (_longPollHeartbeart != null)
            {
                Platform.RemoveAction(_longPollHeartbeart);
                _longPollHeartbeart = null;
            }
        }

        private void DisconnectLongPollerIfPossible()
        {
            if (_longPollClient != null && _longPollClient.IsConnected)
            {
                _longPollerAborted = true;
                try
                {
                    TelegramUtils.RunSynchronously(_longPollClient.Disconnect());
                }
                catch (Exception ex)
                {
                    DebugPrint("Failed to disconnect full client: " + ex);
                }
                RemoveLongPollPingIfPossible();
            }
        }

        private ulong GetRandomId()
        {
            var buffer = new byte[sizeof(ulong)];
            _random.NextBytes(buffer);
            return BitConverter.ToUInt32(buffer, 0);
        }

        public Telegram()
        {
            _baseMessageId = Convert.ToString(Time.GetNowUnixTimestamp());
        }

        public override bool Initialize(DisaSettings settings)
        {
            _settings = settings as TelegramSettings;
            _mutableSettings = MutableSettingsManager.Load<TelegramMutableSettings>();

            if (_settings.AuthKey == null)
            {
                return false;
            }

            return true;
        }

        public override bool InitializeDefault()
        {
            return false;
        }

        public override bool Authenticate(WakeLock wakeLock)
        {
            return true;
        }

        public override void Deauthenticate()
        {
            // do nothing
        }

        private void FetchDifference(TelegramClient client)
        {
            var counter = 0;

            DebugPrint("Fetching difference");

            //sometimes fetchdifference gives new channel message, which updates the state of the channel 
            //which leads to missed messasges, hence we should first fetch difference for the channels, then fetch the total 
            //difference.
            FetchChannelDifference(client);

        Again:

            DebugPrint("Difference Page: " + counter);

            Utils.DebugPrint(">>> Current State PTS: " + _mutableSettings.Pts + " QTS: " + _mutableSettings.Qts + " Date: " + _mutableSettings.Date);

            var difference = TelegramUtils.RunSynchronously(
                client.Methods.UpdatesGetDifferenceAsync(new UpdatesGetDifferenceArgs
            {
                Date = _mutableSettings.Date,
                Pts = _mutableSettings.Pts,
                Qts = _mutableSettings.Qts
            }));

            var empty = difference as UpdatesDifferenceEmpty;
            var diff = difference as UpdatesDifference;
            var slice = difference as UpdatesDifferenceSlice;

            Action dispatchUpdates = () =>
            {
                var updates = new List<object>();
                //TODO: encrypyed messages
                if (diff != null)
                {
                    updates.AddRange(diff.NewMessages);
                    updates.AddRange(diff.OtherUpdates);
                }
                else if(slice!=null)
                {
                    updates.AddRange(slice.NewMessages);
                    updates.AddRange(slice.OtherUpdates);
                }
                DebugPrint(ObjectDumper.Dump(updates));
                ProcessIncomingPayload(updates, false, client);
            };

            if (diff != null)
            {
				_dialogs.AddUsers(diff.Users);
				_dialogs.AddChats(diff.Chats);
                dispatchUpdates();
                var state = (UpdatesState)diff.State;
				SaveState(state.Date, state.Pts, state.Qts, state.Seq);
            }
            else if (slice != null)
            {
				_dialogs.AddUsers(slice.Users);
				_dialogs.AddChats(slice.Chats);
                dispatchUpdates();
                var state = (UpdatesState)slice.IntermediateState;
				SaveState(state.Date, state.Pts, state.Qts, state.Seq);
                counter++;
                goto Again;
            }
            else if (empty != null)	
            {
                SaveState(empty.Date, 0, 0, empty.Seq);
            }
        }

        private void FetchChannelDifference(TelegramClient client)
        {
            var extendedBubbleGroups = BubbleGroupManager.FindAll(this).Where(group => group.IsExtendedParty);
            foreach (var bubbleGroup in extendedBubbleGroups)
            {
                try
                {
                Again:
                    var channelAddress = uint.Parse(bubbleGroup.Address);
                    var channel = _dialogs.GetChat(uint.Parse(bubbleGroup.Address));
                    var channelObj = channel as Channel;
                    if (channelObj != null)
                    {
                        Utils.DebugPrint("Channel name " + (channelObj.Title));
                    }
                    else
                    {
                        Utils.DebugPrint("### There is no channel in the database, reconstructing it from server");
                        FetchNewDialogs(client);
                    }
                    var channelPts = _dialogs.GetChatPts(channelAddress);
                    if (channelPts == 0)
                    {
                        uint pts = GetLastPtsForChannel(client, channelAddress.ToString());
                        SaveChannelState(channelAddress, pts);
                    }
                    Utils.DebugPrint("Channel Pts " + channelPts);
                    var result = TelegramUtils.RunSynchronously(
                        client.Methods.UpdatesGetChannelDifferenceAsync(new UpdatesGetChannelDifferenceArgs
                        {
                            Channel = new InputChannel
                            {
                                ChannelId = channelAddress,
                                AccessHash = TelegramUtils.GetChannelAccessHash(_dialogs.GetChat(channelAddress))
                            },
                            Filter = new ChannelMessagesFilterEmpty(),
                            Limit = 100,
                            Pts = channelPts
                        }));
                    var updates = ProcessChannelDifferenceResult(channelAddress, result);
                    if (updates.Any())
                    {
                        ProcessIncomingPayload(updates, false, client);
                        goto Again;
                    }
                }
                catch (Exception e)
                {
                    DebugPrint("#### Exception getting channels" + e);
                    continue;
                }
            }
        }

        private uint GetLastMessageIdService(BubbleGroup bubbleGroup)
        {
            var id = bubbleGroup.LastBubbleSafe().IdService;
            if (id == null)
            {
                return 0;
            }
            return uint.Parse(id);
        }

        private List<object> ProcessChannelDifferenceResult(uint channelId, IUpdatesChannelDifference result)
        {
            var updatesChannelDifference = result as UpdatesChannelDifference;
            var updatesChannelDifferenceEmpty = result as UpdatesChannelDifferenceEmpty;
            var updatesChannelDifferenceTooLong = result as UpdatesChannelDifferenceTooLong;

            var updatesList = new List<object>();

            if (updatesChannelDifference != null)
			{
                updatesList.AddRange(updatesChannelDifference.OtherUpdates);
                updatesList.AddRange(updatesChannelDifference.Chats);
				updatesList.AddRange(updatesChannelDifference.NewMessages);
                SaveChannelState(channelId, updatesChannelDifference.Pts);
            }
            else if (updatesChannelDifferenceEmpty != null)
            {
                SaveChannelState(channelId, updatesChannelDifferenceEmpty.Pts);
            }
            else if (updatesChannelDifferenceTooLong != null)
            {
				updatesList.AddRange(updatesChannelDifferenceTooLong.Users);
				updatesList.AddRange(updatesChannelDifferenceTooLong.Chats);
				updatesList.AddRange(updatesChannelDifferenceTooLong.Messages);
                SaveChannelState(channelId, updatesChannelDifferenceTooLong.Pts);
            }
            return updatesList;
        }

        private void FetchState(TelegramClient client)
        {
            if (_mutableSettings.Date == 0)
            {
                DebugPrint("We need to fetch the state!");
                var state = (UpdatesState)TelegramUtils.RunSynchronously(client.Methods.UpdatesGetStateAsync(new UpdatesGetStateArgs()));
                SaveState(state.Date, state.Pts, state.Qts, state.Seq);
            }
            else
            {
                FetchDifference(client);
            }
        }

        public override void Connect(WakeLock wakeLock)
        {
            Disconnect();
            var sessionId = GetRandomId();
            var transportConfig = 
                new TcpClientTransportConfig(_settings.NearestDcIp, _settings.NearestDcPort);
            using (var client = new TelegramClient(transportConfig, 
                                    new ConnectionConfig(_settings.AuthKey, _settings.Salt), AppInfo))
            {
				try
				{
					var result = TelegramUtils.RunSynchronously(client.Connect());
					if (result != MTProtoConnectResult.Success)
					{
						throw new Exception("Failed to connect: " + result);
					}
				
	                DebugPrint("Registering long poller...");
	                var registerDeviceResult = TelegramUtils.RunSynchronously(client.Methods.AccountRegisterDeviceAsync(
	                    new AccountRegisterDeviceArgs
	                {
	                    TokenType = 7,
	                    Token = sessionId.ToString(CultureInfo.InvariantCulture),
	                }));
	                if (!registerDeviceResult)
	                {
	                    throw new Exception("Failed to register long poller...");
	                }
				}
				catch (Exception ex)
				{
					Utils.DebugPrint("Exception while connecting telegram! " + ex);
					if (ex.Message.Contains("USER_DEACTIVATED"))
					{
						ResetTelegram();
					}
					throw ex;
				}


				DebugPrint(">>>>>>>>>>>>>> Fetching state!");
                FetchState(client);
                DebugPrint (">>>>>>>>>>>>>> Fetching dialogs!");
                GetDialogs(client);
                GetConfig(client);
            }

            DebugPrint("Starting long poller...");
            if (_longPollClient != null)
            {
                _longPollClient.OnUpdateTooLong -= OnLongPollClientUpdateTooLong;
                _longPollClient.OnClosedInternally -= OnLongPollClientClosed;
            }
            _longPollerAborted = false;
            _longPollClient = new TelegramClient(transportConfig, 
                new ConnectionConfig(_settings.AuthKey, _settings.Salt) { SessionId = sessionId }, AppInfo);
            var result2 = TelegramUtils.RunSynchronously(_longPollClient.Connect());
            if (result2 != MTProtoConnectResult.Success)
            {
                throw new Exception("Failed to connect long poll client: " + result2);
            } 
            _longPollClient.OnUpdateTooLong += OnLongPollClientUpdateTooLong;
            _longPollClient.OnClosedInternally += OnLongPollClientClosed;
           	ScheduleLongPollPing();
            DebugPrint("Long poller started!");
        }

		private void ResetTelegram()
		{
			SettingsManager.Delete(this);
		}

		public override void Disconnect()
        {
            DisconnectFullClientIfPossible();
            DisconnectLongPollerIfPossible();
        }

        public override string GetIcon(bool large)
        {
            if (large)
            {
                return Constants.LargeIcon;
            }

            return Constants.SmallIcon;
        }

        public override IEnumerable<Bubble> ProcessBubbles()
        {
            throw new NotImplementedException();
        }

        public override void SendBubble(Bubble b)
        {
            var presenceBubble = b as PresenceBubble;
            if (presenceBubble != null)
            {
                _hasPresence = presenceBubble.Available;
                SetFullClientPingDelayDisconnect();
                using (var client = new FullClientDisposable(this))
                {
                    if (_hasPresence)
                    {
                        if (_mutableSettings.Date != 0)
                        {
                            // as thy come forth, thy shall get difference
                            FetchDifference(client.Client);
                        }
                        var updatedUsers = GetUpdatedUsersOfAllDialogs(client.Client);
                        if (updatedUsers != null)
                        {
                            foreach (var updatedUser in updatedUsers)
                            {
                                TelegramEventBubble(new PresenceBubble(Time.GetNowUnixTimestamp(), Bubble.BubbleDirection.Incoming,
                                    TelegramUtils.GetUserId(updatedUser), false, this, TelegramUtils.GetAvailable(updatedUser)));
                            }
                        }
                    }
                    SendPresence(client.Client);
                }
            }

            var typingBubble = b as TypingBubble;
            if (typingBubble != null)
            {
                var peer = GetInputPeer(typingBubble.Address, typingBubble.Party, typingBubble.ExtendedParty);
                using (var client = new FullClientDisposable(this))
                {
                    TelegramUtils.RunSynchronously(client.Client.Methods.MessagesSetTypingAsync(
                        new MessagesSetTypingArgs
                        {
                            Peer = peer,
                            Action = typingBubble.IsAudio ? (ISendMessageAction)new SendMessageRecordAudioAction() : (ISendMessageAction)new SendMessageTypingAction()
                        }));
                }
            }

            var textBubble = b as TextBubble;
            if (textBubble != null)
            {
                var peer = GetInputPeer(textBubble.Address, textBubble.Party, textBubble.ExtendedParty);

                using (var client = new FullClientDisposable(this))
                {
                    var iUpdates = TelegramUtils.RunSynchronously(
                        client.Client.Methods.MessagesSendMessageAsync(new MessagesSendMessageArgs
                        {
                            Flags = 0,
                            Peer = peer,
                            Message = textBubble.Message,
                            RandomId = ulong.Parse(textBubble.IdService2)
                        }));
                    DebugPrint(">>>> IUpdates message sent " + ObjectDumper.Dump(iUpdates));
                    var updateShortSentMessage = iUpdates as UpdateShortSentMessage;
                    if (updateShortSentMessage != null)
                    {
                        textBubble.IdService = updateShortSentMessage.Id.ToString(CultureInfo.InvariantCulture);
                    }
                    var updates = iUpdates as Updates;
                    if (updates != null)
                    {
                        foreach (var update in updates.UpdatesProperty)
                        {
							var updateMessageId = update as UpdateMessageID;
							if (updateMessageId != null)
							{
								textBubble.IdService = updateMessageId.Id.ToString(CultureInfo.InvariantCulture);
								break;
							}
                            var updateNewChannelMessage = update as UpdateNewChannelMessage;
                            if (updateNewChannelMessage == null) 
								continue;
                            var message = updateNewChannelMessage.Message as Message;
                            if (message != null)
                            {
                                textBubble.IdService = message.Id.ToString(CultureInfo.InvariantCulture);
								break;
                            }
                        }
                    }
                    SendToResponseDispatcher(iUpdates, client.Client);
                }
            }

            var readBubble = b as ReadBubble;
            if (readBubble != null)
            {
                using (var client = new FullClientDisposable(this))
                {
                    var peer = GetInputPeer(readBubble.Address, readBubble.Party, readBubble.ExtendedParty);
                    if (peer is InputPeerChannel)
                    {
                        TelegramUtils.RunSynchronously(client.Client.Methods.ChannelsReadHistoryAsync(new ChannelsReadHistoryArgs
                        {
                            Channel = new InputChannel
                            {
                                ChannelId = uint.Parse(readBubble.Address),
                                AccessHash = TelegramUtils.GetChannelAccessHash(_dialogs.GetChat(uint.Parse(readBubble.Address)))
                            },
                            MaxId = 0
                        }));
                    }
                    else
                    {
                        var messagesAffectedMessages =
                            TelegramUtils.RunSynchronously(client.Client.Methods.MessagesReadHistoryAsync(
                            new MessagesReadHistoryArgs
                            {
                                Peer = peer,
                                MaxId = 0,

                            })) as MessagesAffectedMessages;
                        //if (messagesAffectedMessages != null)
                        //{
                        //    SaveState(0, messagesAffectedMessages.Pts, 0, 0);
                        //}
                    }
                }
            }

            var imageBubble = b as ImageBubble;
            if (imageBubble != null)
            {
                var fileId = GenerateRandomId();
                try
                {
                    var inputFile = UploadFile(imageBubble, fileId, 0);
                    SendFile(imageBubble, inputFile);
                }
                catch (Exception e)
                {
                    DebugPrint("File upload error " + e);
                    throw;
                }
            }

            var fileBubble = b as FileBubble;

            if (fileBubble != null)
            {
                var fileId = GenerateRandomId();
                try
                {
                    var fileInfo = new FileInfo(fileBubble.Path);
                    DebugPrint(">>>>>>> the size of the file is " + fileInfo.Length);
                    if (fileInfo.Length <= 10485760)
                    {
                        var inputFile = UploadFile(fileBubble, fileId, fileInfo.Length);
                        SendFile(fileBubble, inputFile);
                    }
                    else
                    {
                        var inputFile = UploadBigFile(fileBubble, fileId, fileInfo.Length);
                        SendFile(fileBubble, inputFile);
                    }
                }
                catch (Exception e)
                {
                    DebugPrint("File upload error " + e);
                    throw;
                }
            }

            var audioBubble = b as AudioBubble;

            if (audioBubble != null)
            {
                var fileId = GenerateRandomId();
                try
                {
                    var fileInfo = new FileInfo(audioBubble.AudioPath);
                    DebugPrint(">>>>>>> the size of the file is " + fileInfo.Length);
                    if (fileInfo.Length <= 10485760)
                    {
                        var inputFile = UploadFile(audioBubble, fileId, fileInfo.Length);
                        SendFile(audioBubble, inputFile);
                    }
                    else
                    {
                        var inputFile = UploadBigFile(audioBubble, fileId, fileInfo.Length);
                        SendFile(audioBubble, inputFile);
                    }
                }
                catch (Exception e)
                {
                    DebugPrint("File upload error " + e);
                    throw;
                }
            }

            var locationBubble = b as LocationBubble;

            if (locationBubble != null)
            {
                SendGeoLocation(locationBubble);
            }

            var contactBubble = b as ContactBubble;

            if (contactBubble != null)
            {
                SendContact(contactBubble);
            }

            DebugPrint(">>>> Sending visual bubble " + ObjectDumper.Dump(b));

        }

        private void SendContact(ContactBubble contactBubble)
        {
            var inputPeer = GetInputPeer(contactBubble.Address, contactBubble.Party, contactBubble.ExtendedParty);
            var contactCard = Platform.GenerateContactCardFromBytes(contactBubble.VCardData);
            if (contactCard != null)
            {
                using (var client = new FullClientDisposable(this))
                {
                    var updates = TelegramUtils.RunSynchronously(
                        client.Client.Methods.MessagesSendMediaAsync(new MessagesSendMediaArgs
                        {

                            Flags = 0,
                            Peer = inputPeer,
                            Media = new InputMediaContact
                            {
                                FirstName = contactCard.GivenName,
                                LastName = contactCard.FamilyName,
                                PhoneNumber = contactCard.Phones?.FirstOrDefault()?.Number
                            },
                            RandomId = GenerateRandomId(),
                        }));
                    var id = GetMessageId(updates);
                    contactBubble.IdService = id;
                    SendToResponseDispatcher(updates, client.Client);
                }
            }
        }

        private void SendGeoLocation(LocationBubble locationBubble)
        {
            var inputPeer = GetInputPeer(locationBubble.Address, locationBubble.Party, locationBubble.ExtendedParty);
            using (var client = new FullClientDisposable(this))
            {
                var updates = TelegramUtils.RunSynchronously(
                    client.Client.Methods.MessagesSendMediaAsync(new MessagesSendMediaArgs
                    {

                        Flags = 0,
                        Peer = inputPeer,
                        Media = new InputMediaGeoPoint
                        {
                            GeoPoint = new InputGeoPoint
                            {
                                Lat = locationBubble.Latitude,
                                Long = locationBubble.Longitude
                            }
                        },
                        RandomId = GenerateRandomId(),
                    }));
                var id = GetMessageId(updates);
                locationBubble.IdService = id;
                SendToResponseDispatcher(updates,client.Client);
            }

        }

        private IInputFile UploadBigFile(VisualBubble bubble, ulong fileId, long fileSize)
        {
            uint chunkSize = 524288;
            var fileTotalParts = (uint)fileSize/chunkSize;
            if (fileSize % 1024 != 0)
            {
                fileTotalParts++; 
            }
            var chunk = new byte[chunkSize];
            uint chunkNumber = 0;
            var offset = 0;
            using (var file = File.OpenRead(GetPathFromBubble(bubble)))
            {
                using (var client = new FullClientDisposable(this))
                {
                    int bytesRead;
                    while ((bytesRead = file.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        var uploaded =
                            TelegramUtils.RunSynchronously(
                                client.Client.Methods.UploadSaveBigFilePartAsync(new UploadSaveBigFilePartArgs
                                {
                                    Bytes = chunk,
                                    FileId = fileId,
                                    FilePart = chunkNumber,
                                    FileTotalParts = fileTotalParts,
                                }));
                        DebugPrint("Uploading File part " + chunkNumber + " of " + fileTotalParts);
                        if (!uploaded)
                        {
                            throw new Exception("The file chunk failed to be uploaded");
                        }
                        chunkNumber++;
                        offset += bytesRead;
                        UpdateSendProgress(bubble, offset, fileSize);
                    }
                    return new InputFileBig
                    {
                        Id = fileId,
                        Name = GetNameFromBubble(bubble),
                        Parts = chunkNumber
                    };
                }
            }

        }

        private int CalculateChunkSize(long fileSize)
        {
            var uploadChunkSize = 32*1024;
            while ((fileSize / (int)uploadChunkSize) > 1000)
            {
                uploadChunkSize *= 2;
            }
            return uploadChunkSize;
        }

        private string GetNameFromBubble(VisualBubble bubble)
        {
            var fileBubble = bubble as FileBubble;
            var audioBubble = bubble as AudioBubble;
            var imageBubble = bubble as ImageBubble;

            if (fileBubble != null)
            {
                return fileBubble.FileName;
            }
            else if (audioBubble != null)
            {
                return audioBubble.AudioPathNative;
            }
            else if(imageBubble!=null)
            {
                return imageBubble.ImagePathNative;
            }
            return null;

        }

        private string GetPathFromBubble(VisualBubble bubble)
        {
            var fileBubble = bubble as FileBubble;
            var audioBubble = bubble as AudioBubble;
            var imageBubble = bubble as ImageBubble;


            if (fileBubble != null)
            {
                return fileBubble.Path;
            }
            else if (audioBubble != null)
            {
                return audioBubble.AudioPath;
            }
            else if(imageBubble!=null)
            {
                return imageBubble.ImagePath;
            }
            return null;

        }

        private IInputFile UploadFile(VisualBubble bubble, ulong fileId, long fileSize)
        {
            int chunkSize = CalculateChunkSize(fileSize);
            var chunk = new byte[chunkSize];
            uint chunkNumber = 0;
            var offset = 0;
            using (var file = File.OpenRead(GetPathFromBubble(bubble)))
            {
                using (var client = new FullClientDisposable(this))
                {
                    int bytesRead;
                    while ((bytesRead = file.Read(chunk, 0, chunk.Length)) > 0)
                    {
						//RPC call
						try
						{
							var uploaded =
								TelegramUtils.RunSynchronously(
									client.Client.Methods.UploadSaveFilePartAsync(new UploadSaveFilePartArgs
									{
										Bytes = chunk,
										FileId = fileId,
										FilePart = chunkNumber
									}));

							if (!uploaded)
							{
								throw new Exception("The file chunk failed to be uploaded");
							}
							chunkNumber++;
							offset += bytesRead;
							UpdateSendProgress(bubble, offset, fileSize);
						}
						catch (Exception ex)
						{
							Utils.DebugPrint("Exception while uploading file " + ex.InnerException);
							throw ex;
						}
                    }
                    return new InputFile
                    {
                        Id = fileId,
                        Md5Checksum = "",
                        Name = GetNameFromBubble(bubble),
                        Parts = chunkNumber
                    };
                    
                }
            }
        }

        private void UpdateSendProgress(VisualBubble bubble, int offset, long fileSize)
        {
            if (fileSize == 0)
            {
                return;
            }
            var fileBubble = bubble as FileBubble;
            var audioBubble = bubble as AudioBubble;
            if(fileBubble!=null)
            {
                float progress = offset / (float)fileSize;
                if (fileBubble.Transfer != null && fileBubble.Transfer.Progress != null)
                {
                    try
                    {
                        fileBubble.Transfer.Progress((int) (progress*100));
                    }
                    catch (Exception ex)
                    {
                    }
                }

            }else if (audioBubble != null)
            {
                float progress = offset / (float)fileSize;
                if (audioBubble.Transfer != null && audioBubble.Transfer.Progress != null)
                {
                    try
                    {
                        audioBubble.Transfer.Progress((int)(progress * 100));
                    }
                    catch (Exception ex)
                    {
                    }
                }
            }
        }

        private string GetMessageId(IUpdates iUpdates)
        {
            DebugPrint(">>>> Get message id updates " + ObjectDumper.Dump(iUpdates));
            var updates = iUpdates as Updates;
            if (updates != null)
            {
                foreach (var update in updates.UpdatesProperty)
                {
                    var updateNewMessage = update as UpdateNewMessage;
                    var updateNewChannelMessage = update as UpdateNewChannelMessage;
                    var updateMessageId = update as UpdateMessageID;
                    if (updateNewMessage != null)
                    {
                        var message = updateNewMessage.Message as Message;
                        return message?.Id.ToString(CultureInfo.InvariantCulture);
                    }
                    if (updateNewChannelMessage != null)
                    {
                        var message = updateNewChannelMessage.Message as Message;
                        return message?.Id.ToString(CultureInfo.InvariantCulture);
                    }
                }
            }
            return null;
        }

        private void SendFile(VisualBubble bubble, IInputFile inputFile)
        {
            var inputPeer = GetInputPeer(bubble.Address, bubble.Party, bubble.ExtendedParty);
            var imageBubble = bubble as ImageBubble;
            var fileBubble = bubble as FileBubble;
            var audioBubble = bubble as AudioBubble;

            using (var client = new FullClientDisposable(this))
            {
                if (imageBubble != null)
                {

                    var iUpdates = TelegramUtils.RunSynchronously(
                        client.Client.Methods.MessagesSendMediaAsync(new MessagesSendMediaArgs
                        {

                            Flags = 0,
                            Peer = inputPeer,
                            Media = new InputMediaUploadedPhoto
                            {
                                Caption = "",
                                File = inputFile,
                            },
                            RandomId = GenerateRandomId(),
                        }));
                    var messageId = GetMessageId(iUpdates);
                    imageBubble.IdService = messageId;
                    var updates = iUpdates as Updates;

                    SendToResponseDispatcher(iUpdates, client.Client);
                }
                else if (fileBubble != null)
                {
                    var documentAttributes = new List<IDocumentAttribute>
                    {
                        new DocumentAttributeFilename
                        {
                            FileName = fileBubble.FileName
                        }
                    };
                    var iUpdates = TelegramUtils.RunSynchronously(
                        client.Client.Methods.MessagesSendMediaAsync(new MessagesSendMediaArgs
                        {

                            Flags = 0,
                            Peer = inputPeer,
                            Media = new InputMediaUploadedDocument
                            {
                                Attributes = documentAttributes,
                                Caption = "",
                                File = inputFile,
                                MimeType = fileBubble.MimeType,
                            },
                            RandomId = GenerateRandomId(),
                        }));
                    var messageId = GetMessageId(iUpdates);
                    fileBubble.IdService = messageId;
                    SendToResponseDispatcher(iUpdates, client.Client);

                }
                else if (audioBubble != null)
                {
                    var documentAttributes = new List<IDocumentAttribute>();

                    if (audioBubble.Recording)
                    {
                        documentAttributes.Add(new DocumentAttributeAudio
                        {
                            Flags = 1024,
                            Duration = (uint)audioBubble.Seconds,
                            Voice = new True()
                        });
                    }
                    else if (audioBubble.FileName != null)
                    {
                        documentAttributes.Add(new DocumentAttributeAudio
                        {
                            Flags = 1,
                            Duration = (uint)audioBubble.Seconds,
                            Title = audioBubble.FileName
                        });
                    }
                    else 
                    {
                        documentAttributes.Add(new DocumentAttributeAudio
                        {
                            Flags = 0,
                            Duration = (uint)audioBubble.Seconds
                        });
                    }

                    var mimeType = Platform.GetMimeTypeFromPath(audioBubble.AudioPath);
                    var inputMedia = new InputMediaUploadedDocument
                    {
                        Attributes = documentAttributes,
                        Caption = "",
                        File = inputFile,
                        MimeType = mimeType,
                    };

                    var media = new MessagesSendMediaArgs
                    {
                        Flags = 0,
                        Media = inputMedia,
                        Peer = inputPeer,
                        RandomId = GenerateRandomId(),
                    };
                    var iUpdates = TelegramUtils.RunSynchronously(
                        client.Client.Methods.MessagesSendMediaAsync(media));
                    var messageId = GetMessageId(iUpdates);
                    audioBubble.IdService = messageId;
                    SendToResponseDispatcher(iUpdates, client.Client);
                }
            }
        }


        public ulong GenerateRandomId()
        {
            byte[] buffer = new byte[sizeof(UInt64)];
            var random = new Random();
            random.NextBytes(buffer);
            var id = BitConverter.ToUInt64(buffer, 0);
            return id;
        }

        void SendToResponseDispatcher(IUpdates iUpdate,TelegramClient client)
        {
                var mtProtoClientConnection = client.Connection as MTProtoClientConnection;
                if (mtProtoClientConnection != null)
                {
                    var responseDispatcher = mtProtoClientConnection.ResponseDispatcher as ResponseDispatcher;
                    if (responseDispatcher != null)
                    {
                        SharpMTProto.Schema.IMessage tempMessage = new SharpMTProto.Schema.Message(0,0,iUpdate);
                        responseDispatcher.DispatchAsync(tempMessage).Wait();
                    }
                }
        }

        private IInputPeer GetInputPeer(string id, bool groupChat, bool superGroup)
        {
            if (superGroup)
            {
                return new InputPeerChannel
                {
                    ChannelId = uint.Parse(id),
                    AccessHash = TelegramUtils.GetChannelAccessHash(_dialogs.GetChat(uint.Parse(id)))
                };
            }
            if (groupChat)
            {
                return new InputPeerChat
                {
                    ChatId = uint.Parse(id)
                };
            }
            var accessHash = GetUserAccessHashIfForeign(id);
            return new InputPeerUser
            {
                UserId = uint.Parse(id),
                AccessHash = accessHash
            };
        }

        private ulong GetUserAccessHashIfForeign(string userId)
        { 
            var user = _dialogs.GetUser(uint.Parse(userId));
            if (user != null)
            {
                return TelegramUtils.GetAccessHash(user);
            }
            return 0;
        }

        public override bool BubbleGroupComparer(string first, string second)
        {
            return first == second;
        }

        public override Task GetBubbleGroupLegibleId(BubbleGroup group, Action<string> result)
        {
            return Task.Factory.StartNew(() =>
            {
                result(null);
            });
        }

        private async Task<List<IUser>> GetUsers(List<IInputUser> users, TelegramClient client)
        {
            var response = await client.Methods.UsersGetUsersAsync(new UsersGetUsersArgs
            {
                Id = users
            });
            return response;
        }

        private async Task<IUser> GetUser(IInputUser user, TelegramClient client)
        {
            return (await GetUsers(new List<IInputUser> { user }, client)).First();
        }

        private async Task<List<User>> FetchContacts()
        {
            if (contactsCache.Count != 0)
            {
                return contactsCache;
            }
            using (var client = new FullClientDisposable(this))
            {
                var response = (ContactsContacts)await client.Client.Methods.ContactsGetContactsAsync(
                    new ContactsGetContactsArgs
                    {
                        Hash = string.Empty
                    });
                contactsCache.AddRange(response.Users.OfType<User>().ToList());
                _dialogs.AddUsers(response.Users);
                return contactsCache;
            }

        }

        public override Task GetBubbleGroupName(BubbleGroup group, Action<string> result)
        {
            DebugPrint("GetBubbleGroupName");
            return Task.Factory.StartNew(() =>
            {
                result(GetTitle(group.Address, group.IsParty));
            });
        }

        public override Task GetBubbleGroupPhoto(BubbleGroup group, Action<DisaThumbnail> result)
        {
            DebugPrint("GetBubbleGroupPhoto");
            return Task.Factory.StartNew(() =>
            {
                result(GetThumbnail(group.Address, group.IsParty, true));
            });
        }

        public override Task GetBubbleGroupPartyParticipants(BubbleGroup group, Action<DisaParticipant[]> result)
        {
            return Task.Factory.StartNew(() =>
            {
                result(null);
            });
        }

        public override Task GetBubbleGroupUnknownPartyParticipant(BubbleGroup group, string unknownPartyParticipant, Action<DisaParticipant> result)
        {
            return Task.Factory.StartNew(() =>
            {
                var name = GetTitle(unknownPartyParticipant, false);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    result(new DisaParticipant(name, unknownPartyParticipant));
                }
                else
                {
                    result(null);
                }
            });
        }

        public override Task GetBubbleGroupPartyParticipantPhoto(DisaParticipant participant, Action<DisaThumbnail> result)
        {
            return Task.Factory.StartNew(() =>
            {
                result(GetThumbnail(participant.Address, false, true));
            });
        }

        public override Task GetBubbleGroupLastOnline(BubbleGroup group, Action<long> result)
        {
            return Task.Factory.StartNew(() =>
            {
                result(GetUpdatedLastOnline(group.Address));
            });
        }

        public void AddVisualBubbleIdServices(VisualBubble bubble)
        {
            bubble.IdService2 = NextMessageId;
        }

        public bool DisctinctIncomingVisualBubbleIdServices()
        {
            return true;
        }

        public override void RefreshPhoneBookContacts()
        {
            Utils.DebugPrint("Phone book contacts have been updated! Sending information to Telegram servers...");
            var contacts = PhoneBook.PhoneBookContacts;
            var inputContacts = new List<IInputContact>();
            foreach (var contact in contacts)
            {
                foreach (var phoneNumber in contact.PhoneNumbers)
                {
                    var clientId = "0";
                    if (contact.ContactId != null)
                    {
                        clientId = contact.ContactId;
                    }
                    inputContacts.Add(new InputPhoneContact
                    {
                        ClientId = ulong.Parse(clientId),
						Phone = FormatNumber(phoneNumber.Number),
                        FirstName = contact.FirstName,
                        LastName = contact.LastName,
                    });
                }
            }
            if (!inputContacts.Any())
            {
                Utils.DebugPrint("There are no input contacts!");
                return;
            }
            try
            {
                using (var client = new FullClientDisposable(this))
                {
                    var importedContacts = TelegramUtils.RunSynchronously(client.Client.Methods.ContactsImportContactsAsync(
                        new ContactsImportContactsArgs
                        {
                            Contacts = inputContacts,
                            Replace = false,
                        }));
                    var contactsImportedcontacts = importedContacts as ContactsImportedContacts;
                    if (contactsImportedcontacts != null)
                    {
                        _dialogs.AddUsers(contactsImportedcontacts.Users);
                    }
                    //invalidate the cache
                    contactsCache = new List<User>();
                }
            }
            catch (Exception ex)
            {
                Utils.DebugPrint("Failed to update contacts: " + ex);
            }
        }

        private IUser GetUser(List<IUser> users, string userId)
        {
            foreach (var user in users)
            {
                var userIdInner = TelegramUtils.GetUserId(user);
                if (userId == userIdInner)
                {
                    return user;
                }
            }
            return null;
        }

        private List<IUser> GetUpdatedUsersOfAllDialogs(TelegramClient client)
        {
            var users = new List<IUser>();
            foreach (var userBubbleGroup in BubbleGroupManager.FindAll(this).Where(x => !x.IsParty))
            {
                var user = _dialogs.GetUser(uint.Parse(userBubbleGroup.Address));
                var inputUser = TelegramUtils.CastUserToInputUser(user);
                var updatedUser = GetUser(inputUser, client).Result;
                _dialogs.AddUser(updatedUser);
                if (user != null)
                {
                    users.Add(updatedUser);
                }
            }

            return users;
        }

        private long GetUpdatedLastOnline(string id)
        {
            var user = _dialogs.GetUser(uint.Parse(id));
            if (user != null)
            {
                return TelegramUtils.GetLastSeenTime(user);
            }
            return 0;
        }

        private string GetTitle(string id, bool group)
        {
            if (id == null)
            {
                return null;
            }
            if (group)
            {
                var chat = _dialogs.GetChat(uint.Parse(id));
                if (chat == null)
                {
                    return null;
                }
                return TelegramUtils.GetChatTitle(chat);
            }
            else
            {
                var user = _dialogs.GetUser(uint.Parse(id));
                if (user == null)
                {
                    return null;
                }
                return TelegramUtils.GetUserName(user);
            }
        }

        private IUser GetUser(string id)
        {
            lock (_quickUserLock)
            {
                try
                {
                    using (var client = new FullClientDisposable(this))
                    {
                        var inputUser = new InputUser();
                        inputUser.UserId = uint.Parse(id);
                        var inputList = new List<IInputUser>();
                        inputList.Add(inputUser);
                        DebugPrint(">>>> inputlist " + ObjectDumper.Dump(inputList));
                        var users =
                            TelegramUtils.RunSynchronously(
                                client.Client.Methods.UsersGetUsersAsync(new UsersGetUsersArgs
                                {
                                    Id = inputList,
                                }));
                        DebugPrint(">>>> get users response " + ObjectDumper.Dump(users));
                        var user = users.FirstOrDefault();
                        return user;
                    }
                }
                catch (Exception e)
                {
                    DebugPrint(">>>>>> exception " + e);
                    return null;
                }
            }
        }

        private void InvalidateThumbnail(string id, bool group, bool small)
        {
            if (id == null)
            {
                return;
            }
            var key = id + group + small;

			DisaThumbnail thumbnail = null;

			_thumbnailCache.TryRemove(key, out thumbnail);

			lock(_cachedThumbnailsLock)
			{
				using (var database = new SqlDatabase<CachedThumbnail>(GetThumbnailDatabasePath()))
				{
					var dbThumbnail = database.Store.Where(x => x.Id == key).FirstOrDefault();
					if (dbThumbnail != null)
					{
						database.Store.Delete(x => x.Id == key);
					}
				}
			}
        }

        private string GetThumbnailDatabasePath()
        {
            if (_thumbnailDatabasePathCached != null)
                return _thumbnailDatabasePathCached;

            var databasePath = Platform.GetDatabasePath();
            if (!Directory.Exists(databasePath))
            {
                Utils.DebugPrint("Creating database directory.");
                Directory.CreateDirectory(databasePath);
            }

            _thumbnailDatabasePathCached = Path.Combine(databasePath, "thumbnailCache.db");
            return _thumbnailDatabasePathCached;
        }


        public DisaThumbnail GetThumbnail(string id, bool group, bool small)
        {
            if (id == null)
            {
                return null;
            }
            var key = id + group + small;


            Func<DisaThumbnail, MemoryStream> convertThumbnailToMemoryStream = thumbnail =>
            {
                MemoryStream memoryStream = new MemoryStream();
                Serializer.Serialize<DisaThumbnail>(memoryStream, thumbnail);
                return memoryStream;
            };


            Func<DisaThumbnail, byte[]> serializeDisaThumbnail = thumbnail =>
            {
                using (var memoryStream = convertThumbnailToMemoryStream(thumbnail))
                {
                    return memoryStream.ToArray();
                }
            };

            Func<DisaThumbnail, DisaThumbnail> cache = thumbnail =>
            {
                if (thumbnail == null)
                {
                    return null;
                }
                lock (_cachedThumbnailsLock)
                {
                    using (var database = new SqlDatabase<CachedThumbnail>(GetThumbnailDatabasePath()))
                    {
                        var bytes = serializeDisaThumbnail(thumbnail);
                        if (bytes == null)
                        {
                            return null;
                        }
                        var cachedThumbnail = new CachedThumbnail
                        {
                            Id = key,
                            ThumbnailBytes = bytes
                        };

                        var dbThumbnail = database.Store.Where(x => x.Id == key).FirstOrDefault();
                        if (dbThumbnail != null)
                        {
                            DebugPrint(">>>>>> Saving cached thumbnail after deletion ");
                            database.Store.Delete(x => x.Id == key);
                            database.Add(cachedThumbnail);
                        }
                        else
                        {
                            DebugPrint(">>>>>> Saving cached thumbnail ");
                            database.Add(cachedThumbnail);
                        }
                    }
                }
				_thumbnailCache[key] = thumbnail;
                return thumbnail;
            };

			if (_thumbnailCache.ContainsKey(key))
				return _thumbnailCache[key];

            lock (_cachedThumbnailsLock)
            {
                using (var database = new SqlDatabase<CachedThumbnail>(GetThumbnailDatabasePath()))
                {
                    Retry:
                    var dbThumbnail = database.Store.Where(x => x.Id == key).FirstOrDefault();
                    if (dbThumbnail != null)
                    {
                        if (dbThumbnail.ThumbnailBytes == null)
                        {
                            return null;
                        }
                        using (var stream = new MemoryStream(dbThumbnail.ThumbnailBytes))
                        {
                            var disaThumbnail = Serializer.Deserialize<DisaThumbnail>(stream);
                            disaThumbnail.Failed = false;
                            if (!File.Exists(disaThumbnail.Location))
                            {
                                DebugPrint("The thumbnail cache was purged! purging the database so that everything is redownloaded");
                                database.DeleteAll();
                                goto Retry;
                            }
							_thumbnailCache[key] = disaThumbnail;
                            return disaThumbnail;
                        }
                    }
                }
            }
            if (group)
            {
                var chat = _dialogs.GetChat(uint.Parse(id));
                DebugPrint(">>>> Getting Thumbail for " + TelegramUtils.GetChatTitle(chat));
                if (chat == null)
                {
                    return null;
                }
                var fileLocation = TelegramUtils.GetChatThumbnailLocation(chat, small);
                if (fileLocation == null)
                {
                    return null;
                }
                else
                {
                    if (_thumbnailDownloadingDictionary.ContainsKey(key))
                    {
                        return null; //there is a thread waiting to download the thumbnail 
                    }
                    else
                    {
                        _thumbnailDownloadingDictionary.TryAdd(key, true);
                    }
                    var bytes = FetchFileBytes(fileLocation);
                    DebugPrint(">>>> Got Thumbnail for " + TelegramUtils.GetChatTitle(chat));
                    bool outValue;
                    if (bytes == null)
                    {
                        _thumbnailDownloadingDictionary.TryRemove(key, out outValue);
                        return null;
                    }
                    else
                    {
                        _thumbnailDownloadingDictionary.TryRemove(key, out outValue);
                        return cache(new DisaThumbnail(this, bytes, key));
                    }
                }
            }
            else
            {
                Func<IUser, DisaThumbnail> getThumbnail = user =>
                {
                    DebugPrint(">>>> Getting Thumbail for " + TelegramUtils.GetUserName(user));
                    var fileLocation = TelegramUtils.GetUserPhotoLocation(user, small);
                    if (fileLocation == null)
                    {
                        return cache(null);
                    }
                    else
                    {
                        if (_thumbnailDownloadingDictionary.ContainsKey(id))
                        {
                            return null; //there is a thread waiting to download the thumbnail 
                        }
                        else
                        {
                            _thumbnailDownloadingDictionary.TryAdd(id, true);
                        }
                        var bytes = FetchFileBytes(fileLocation);
                        DebugPrint(">>>> Got Thumbail for " + TelegramUtils.GetUserName(user));
                        bool outValue;
                        if (bytes == null)
                        {
                            _thumbnailDownloadingDictionary.TryRemove(id, out outValue);
                            return null;
                        }
                        else
                        {
                            _thumbnailDownloadingDictionary.TryRemove(id, out outValue);
                            return cache(new DisaThumbnail(this, bytes, key));
                        }
                    }
                };

                var userImg = _dialogs.GetUser(uint.Parse(id));
                return userImg == null ? null : getThumbnail(userImg);
            }
        }

        private static byte[] FetchFileBytes(TelegramClient client, FileLocation fileLocation)
        {
            try
            {
                var response = (UploadFile)TelegramUtils.RunSynchronously(client.Methods.UploadGetFileAsync(
                    new UploadGetFileArgs
                    {
                        Location = new InputFileLocation
                        {
                            VolumeId = fileLocation.VolumeId,
                            LocalId = fileLocation.LocalId,
                            Secret = fileLocation.Secret
                        },
                        Offset = 0,
                        Limit = uint.MaxValue,
                    }));
                return response.Bytes;
            }
            catch (Exception ex)
            {
                Utils.DebugPrint("Exception while fetching file bytes");
                return null;
            }

        }

        private static byte[] FetchFileBytes(TelegramClient client, FileLocation fileLocation, uint offset, uint limit)
        {
            var response = (UploadFile)TelegramUtils.RunSynchronously(client.Methods.UploadGetFileAsync(
                new UploadGetFileArgs
                {
                    Location = new InputFileLocation
                    {
                        VolumeId = fileLocation.VolumeId,
                        LocalId = fileLocation.LocalId,
                        Secret = fileLocation.Secret
                    },
                    Offset = offset,
                    Limit = limit,
                }));
            return response.Bytes;
        }

        private byte[] FetchFileBytes(FileLocation fileLocation)
        {
            if (fileLocation.DcId == _settings.NearestDcId)
            {
                using (var clientDisposable = new FullClientDisposable(this))
                {
                    return FetchFileBytes(clientDisposable.Client, fileLocation);
                }   
            }
            else
            {
                try
                {
                    var client = GetClient((int)fileLocation.DcId);
                    return FetchFileBytes(client, fileLocation);
                }
                catch (Exception ex)
                {
                    DebugPrint("Failed to obtain client from DC manager: " + ex);
                    return null;
                }
            }
        }


        private byte[] FetchFileBytes(FileLocation fileLocation,uint offset,uint limit)
        {
            if (fileLocation.DcId == _settings.NearestDcId)
            {
                using (var clientDisposable = new FullClientDisposable(this))
                {
                    return FetchFileBytes(clientDisposable.Client, fileLocation,offset,limit);
                }
            }
            else
            {
                try
                {
                    var client = GetClient((int)fileLocation.DcId);
                    return FetchFileBytes(client, fileLocation,offset,limit);
                }
                catch (Exception ex)
                {
                    DebugPrint("Failed to obtain client from DC manager: " + ex);
                    return null;
                }
            }
        }

        private void GetConfig(TelegramClient client)
        {
			DebugPrint (">>>>>>>>>>>>>> Getting config!");
            var config = (Config)TelegramUtils.RunSynchronously(client.Methods.HelpGetConfigAsync(new HelpGetConfigArgs
                {
                }));
            _config = config;
			DebugPrint (ObjectDumper.Dump(_config));
        }
 

        private void GetDialogs(TelegramClient client)
        {
            DebugPrint("Fetching conversations");

            _dialogs = new CachedDialogs();

            if (!_dialogs.DatabasesExist())
            {
                FetchNewDialogs(client);
                return;
            }

            DebugPrint("Obtained conversations.");
        }

        private void FetchNewDialogs(TelegramClient client)
        {
            DebugPrint("Databases dont exist! creating new ones from data from server");
            var iDialogs =
                TelegramUtils.RunSynchronously(client.Methods.MessagesGetDialogsAsync(new MessagesGetDialogsArgs
                {
                    Limit = 100,
                    OffsetPeer = new InputPeerEmpty(),
                }));
            var messagesDialogs = iDialogs as MessagesDialogs;

            var messagesDialogsSlice = iDialogs as MessagesDialogsSlice;

            if (messagesDialogs != null)
            {
                _dialogs.AddChats(messagesDialogs.Chats);
                _dialogs.AddUsers(messagesDialogs.Users);
                if (LoadConversations)
                {
                    LoadLast10Conversations(messagesDialogs);
                }
            }
            else if (messagesDialogsSlice != null)
            {
                //first add whatever we have got until now
                _dialogs.AddChats(messagesDialogsSlice.Chats);
                _dialogs.AddUsers(messagesDialogsSlice.Users);
                if (LoadConversations)
                {
                    LoadLast10Conversations(messagesDialogsSlice);
                }
                var numDialogs = 0;
                do
                {
                    numDialogs = messagesDialogsSlice.Dialogs.Count;

                    DebugPrint("%%%%%%% Number of Dialogs " + numDialogs);

                    var lastDialog = messagesDialogsSlice.Dialogs.LastOrDefault() as Dialog;

                    DebugPrint("%%%%%%% Last Dialog " + ObjectDumper.Dump(lastDialog));

                    if (lastDialog != null)
                    {
                        var lastPeer = GetInputPeerFromIPeer(lastDialog.Peer);
                        DebugPrint("%%%%%%% Last Peer " + ObjectDumper.Dump(lastPeer));
                        var offsetId = Math.Max(lastDialog.ReadInboxMaxId, lastDialog.TopMessage);
                        DebugPrint("%%%%%%% message offset " + ObjectDumper.Dump(offsetId));
                        var offsetDate = FindDateForMessageId(offsetId, messagesDialogsSlice);
                        DebugPrint("%%%%%%% offset date " + ObjectDumper.Dump(offsetDate));
                        var nextDialogs = TelegramUtils.RunSynchronously(client.Methods.MessagesGetDialogsAsync(new MessagesGetDialogsArgs
                        {
                            Limit = 100,
                            OffsetPeer = lastPeer,
                            OffsetId = offsetId,
                            OffsetDate = offsetDate
                        }));

                        messagesDialogsSlice = nextDialogs as MessagesDialogsSlice;
                        if (messagesDialogsSlice == null)
                        {
                            DebugPrint("%%%%%%% Next messages dialogs null ");
                            break;
                        }

                        DebugPrint("%%%%%%% users " + ObjectDumper.Dump(messagesDialogsSlice.Users));

                        DebugPrint("%%%%%%% chats " + ObjectDumper.Dump(messagesDialogsSlice.Chats));

                        _dialogs.AddUsers(messagesDialogsSlice.Users);
                        _dialogs.AddChats(messagesDialogsSlice.Chats);
                    }

                    DebugPrint("%%%%%%% Number of Dialogs At end " + numDialogs);

                } while (numDialogs >= 100);
            }
        }

        private void LoadLast10Conversations(IMessagesDialogs iMessagesDialogs)
        {
            var messagesDialogs = iMessagesDialogs as MessagesDialogs;
            var messagesDialogsSlice = iMessagesDialogs as MessagesDialogsSlice;

            if (messagesDialogs != null)
            {
                if (messagesDialogs.Dialogs.Count >= 10)
                {
                    LoadMessages(messagesDialogs.Dialogs, messagesDialogs.Messages);
                }
            }
            if (messagesDialogsSlice != null)
            {
                if (messagesDialogsSlice.Dialogs.Count >= 10)
                {
                    LoadMessages(messagesDialogsSlice.Dialogs,messagesDialogsSlice.Messages);
                }
            }

        }

        private void LoadMessages(List<IDialog> dialogs, List<IMessage> messages)
        {
            int i = 0;

            Action<IMessage> processMessage = iMessage =>
            {
                if (iMessage != null)
                {
                    var message = iMessage as Message;
                    var messageService = iMessage as MessageService;
                    if (message != null)
                    {
                        DebugPrint(">>>>> Message " + ObjectDumper.Dump(message));
                        var bubbles = ProcessFullMessage(message, false);
                        foreach (var bubble in bubbles)
                        {
                            TelegramEventBubble(bubble);
                        }
                    }
                    if (messageService != null)
                    {
						var partyInformationBubbles = MakePartyInformationBubble(messageService, false, null);
                        foreach (var partyInformationBubble in partyInformationBubbles)
                        {
                            TelegramEventBubble(partyInformationBubble);
                        }
                    }
                }
            };

            foreach (var idialog in dialogs)
            {
                var dialog = idialog as Dialog;
                if (dialog != null)
                {
                    var iMessage  = FindMessage(dialog.TopMessage, messages);
                    processMessage(iMessage);
                    if (dialog.UnreadCount == 0)
                    {
                        SetRead(iMessage);
                    }
                    if (dialog.Peer is PeerChannel)
                    { 
                        SaveChannelState(uint.Parse(TelegramUtils.GetPeerId(dialog.Peer)), dialog.Pts);
                    }
                    if (i >= 10)
                        break;
                    i++;
                }
            }
        }

        private void SetRead(IMessage iMessage)
        {
            var message = iMessage as Message;
            var messageService = iMessage as MessageService;


            if (message != null)
            {
                var direction = message.FromId == _settings.AccountId
                    ? Bubble.BubbleDirection.Outgoing
                    : Bubble.BubbleDirection.Incoming;

                var peerUser = message.ToId as PeerUser;
                var peerChat = message.ToId as PeerChat;
                var peerChannel = message.ToId as PeerChannel;

                if (peerUser != null)
                {
                    var address = direction == Bubble.BubbleDirection.Incoming
                        ? message.FromId
                        : peerUser.UserId;
                    BubbleGroupManager.SetUnread(this, false, address.ToString(CultureInfo.InvariantCulture));
                    NotificationManager.Remove(this, address.ToString(CultureInfo.InvariantCulture));
                }
                else if (peerChat != null)
                {
                    BubbleGroupManager.SetUnread(this, false, peerChat.ChatId.ToString(CultureInfo.InvariantCulture));
                    NotificationManager.Remove(this, peerChat.ChatId.ToString(CultureInfo.InvariantCulture));
                }
                else if (peerChannel != null)
                { 
                    BubbleGroupManager.SetUnread(this, false, peerChannel.ChannelId.ToString(CultureInfo.InvariantCulture));
                    NotificationManager.Remove(this, peerChannel.ChannelId.ToString(CultureInfo.InvariantCulture));
                }
            }

            if (messageService != null)
            {
                var address = TelegramUtils.GetPeerId(messageService.ToId);
                BubbleGroupManager.SetUnread(this, false, address);
                NotificationManager.Remove(this, address);
            }

        }

        private IMessage FindMessage(uint topMessage, List<IMessage> messages)
        {
            foreach (var iMessage in messages)
            {
                var messageId = TelegramUtils.GetMessageId(iMessage);
                if (messageId == topMessage)
                {
                    return iMessage;
                }
            }
            return null;
        }

        private IInputPeer GetInputPeerFromIPeer(IPeer peer)
        {
            IInputPeer retInputUser = null;
            var peerUser = peer as PeerUser;
            var peerChat = peer as PeerChat;
            var peerChannel = peer as PeerChannel;

            if (peerUser != null)
            {
                var inputPeerUser = new InputPeerUser
                {
                    UserId = peerUser.UserId
                };
                retInputUser = inputPeerUser;
            }
            else if (peerChat != null)
            {
                var inputPeerChat = new InputPeerChat
                {
                    ChatId = peerChat.ChatId
                };
                retInputUser = inputPeerChat;
            }
            else if(peerChannel!= null)
            {
                var inputPeerChannel = new InputPeerChannel
                {
                    ChannelId = peerChannel.ChannelId,
                    AccessHash = TelegramUtils.GetChannelAccessHash(_dialogs.GetChat(peerChannel.ChannelId))
                };
                retInputUser = inputPeerChannel;
            }
            return retInputUser;

        }

        private uint FindDateForMessageId(uint offsetId, MessagesDialogsSlice messagesDialogsSlice)
        {
            uint date = 0;
            foreach (var iMessage in messagesDialogsSlice.Messages)
            {
                var message = iMessage as Message;
                if (message != null)
                {
                    if (message.Id == offsetId)
                    {
                        date = message.Date;
                    }
                }
                var messageService = iMessage as MessageService;
                if (messageService != null)
                {
                    if (messageService.Id == offsetId)
                    {
                        date = messageService.Date;
                    }
                }

            }
            return date;
        }

        private byte[] FetchDocumentBytes(Document document, uint offset, uint limit)
        {
            if (document.DcId == _settings.NearestDcId)
            {
                using (var clientDisposable = new FullClientDisposable(this))
                {
                    return FetchDocumentBytes(clientDisposable.Client, document, offset, limit);
                }
            }
            else
            {
                try
                {
                    if (cachedClient == null)
                    {
                        cachedClient = GetClient((int) document.DcId);
                    }
                    return FetchDocumentBytes(cachedClient, document, offset, limit);
                }
                catch (Exception ex)
                {
                    DebugPrint("Failed to obtain client from DC manager: " + ex);
                    return null;
                }
            }
        }

        private byte[] FetchDocumentBytes(TelegramClient client, Document document, uint offset, uint limit)
        {
            var response = (UploadFile)TelegramUtils.RunSynchronously(client.Methods.UploadGetFileAsync(
                new UploadGetFileArgs
                {
                    Location = new InputDocumentFileLocation
                    {
                        AccessHash = document.AccessHash,
                        Id = document.Id
                    },
                    Offset = offset,
                    Limit = limit,
                }));
            return response.Bytes;
        }
    }
}

