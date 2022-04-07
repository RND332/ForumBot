using Discord;
using Discord.WebSocket;
using DiscourseApi;
using HtmlAgilityPack;
using System.Text.RegularExpressions;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Extensions.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace ForumBot 
{
    delegate void NewPostFound(string username, string id);
    delegate void NewCommentFound(string username, string id, string text);
    public class Program 
    {
        public static void Main() 
        {
            Forum forum = new Forum("discourse_api_key", );
                "RND332", 
                "https://forum.gton.capital/", 
                "ForumBot", 
                "logs.txt");

            var telegram = new TelegramModule();
            telegram.StartBot();

            var discord = new DiscordModule();
            discord.Start();

            forum.PostFound += NotifyAboutNewPost;
            forum.PostFound += telegram.OnNewPost;
            forum.PostFound += discord.OnNewPost;

            forum.CommentFound += NotifyCommentFound;
            forum.CommentFound += telegram.OnNewComment;
            forum.CommentFound += discord.OnNewComment;

            Console.ReadLine();
        }

        private static void NotifyCommentFound(string username, string id, string text)
        {
            Console.WriteLine(id);
        }

        private static void NotifyAboutNewPost(string username, string id)
        {
            Console.WriteLine(id);
        }
    }
    class Forum
    {
        public event NewPostFound? PostFound;
        public event NewCommentFound? CommentFound;
        private List<int> PostIDs = new List<int>();
        private Dictionary<int, List<int>> CommentsIDs = new Dictionary<int, List<int>>();
        public Api Api { get; }
        public Settings Settings { get; }
        public Forum(string ApiKey, string ApiUsername, string Server, string ApplicationName, string Filename) 
        {
            this.Settings = new Settings();
            Settings.ApiKey = ApiKey;
            Settings.ApiUsername = ApiUsername;
            Settings.ServerUri = new Uri(Server);
            Settings.ApplicationName = ApplicationName;
            Settings.Filename = Filename;

            Api = new DiscourseApi.Api(Settings);

            PostIDs.AddRange(this.GetLatestIdeas().Result);
            PostIDs.AddRange(this.GetLatestProposals().Result);
            
            CommentsIDs = GetLatestComments().Result;

            Thread PostWatcher = new Thread(new ThreadStart(this.PostWatcher));
            Thread CommentWatcher = new Thread(new ThreadStart(this.CommentWatcher));

            PostWatcher.Start();
            CommentWatcher.Start();
        }
        private async Task<IEnumerable<int>> GetLatestIdeas() 
        {
            try
            {
                var posts = await DiscourseApi.Topic.ListAll(this.Api, 5);
                return posts.topic_list.topics.Select(x => x.id);
            }
            catch (Exception ex) 
            {
                return Enumerable.Empty<int>();
            }
        }
        private async Task<IEnumerable<int>> GetLatestProposals()
        {
            try
            {
                var posts = await DiscourseApi.Topic.ListAll(this.Api, 7);
                return posts.topic_list.topics.Select(x => x.id);
            }
            catch (Exception ex)
            {
                return Enumerable.Empty<int>();
            }
        }
        private async Task<Dictionary<int, List<int>>?> GetLatestComments() 
        {
            try
            {
                var Proposals = await GetLatestProposals();
                var Ideas = await GetLatestIdeas();

                var topics = new List<int>();
                topics.AddRange(Proposals);
                topics.AddRange(Ideas);

                var _topic = new Dictionary<int, List<int>>();

                foreach (var topic in topics)
                {
                    var _topic_tmp = await DiscourseApi.Topic.Get(this.Api, topic);

                    var comments = _topic_tmp.post_stream.posts.Select(x => int.Parse(x.post_number.ToString())).Distinct();
                    _topic.Add(topic, comments.ToList());
                }

                return _topic;
            }
            catch (Exception ex)
            {
                return null;
            }
        }
        private async Task<FullTopic?> GetTopic(int topicID) 
        {
            try
            {
                var _topic_tmp = await DiscourseApi.Topic.Get(this.Api, topicID);
                return _topic_tmp;
            }
            catch (Exception) 
            {
                return null;
            }
        }
        public async Task<string> GetPostTitle(int Id) 
        {
            var x = await DiscourseApi.Topic.Get(this.Api, Id);
            return x.fancy_title;
        }
        private async void PostWatcher() 
        {
            while (true)
            {
                var Garbage = new List<int>();
                var ideas = await this.GetLatestIdeas();
                var proposals = await this.GetLatestProposals();

                Garbage.AddRange(ideas);
                Garbage.AddRange(proposals);

                var NewPosts = Garbage.Except(PostIDs);

                if (NewPosts.Count() != 0)
                {
                    foreach (var x in NewPosts)
                    {
                        var tmp = await this.GetTopic(x);
                        if (tmp == null) continue;
                        PostFound?.Invoke(tmp.user_id.ToString(), x.ToString());
                    }
                    PostIDs = Garbage;
                }
                Thread.Sleep(1000);
            }
        }
        private async void CommentWatcher()
        {
            while (true)
            {
                try
                {
                    var comments = await this.GetLatestComments();
                    if (comments == null) continue;
                    foreach (var post in comments.Keys)
                    {
                        if (PostIDs.Contains(post))
                        {
                            var NewComments = comments[post].Except(this.CommentsIDs[post]);
                            foreach (var NewComment in NewComments)
                            {
                                var tmp = await this.GetTopic(post);
                                var clean_from_html = Regex.Replace(tmp.post_stream.posts[NewComment - 1].cooked, "<.*?>", String.Empty);   // clear from html tag's
                                var clean_useless_space = Regex.Replace(clean_from_html, @"\s+", " ");                                      // clean useless space 

                                CommentFound?.Invoke(tmp.post_stream.posts[NewComment - 1].display_username, 
                                    post + "/" + NewComment.ToString(),
                                    clean_useless_space);
                                this.CommentsIDs[post].Add(NewComment);
                            }
                        }
                        else
                        {
                            var tmp = await this.GetTopic(post);
                            PostFound?.Invoke(tmp.post_stream.posts[0].display_username.ToString(), post.ToString());
                            this.PostIDs.Add(post);
                        }
                    }
                    Thread.Sleep(1000);
                }
                catch (Exception ex) 
                {
                    continue;
                }
            }
        }
    }
    class TelegramModule 
    {
        public static TelegramBotClient? Bot;
        public long ChatId { get; private set; } 
        public async Task StartBot()
        {
            Bot = new TelegramBotClient("Discordbot_api_key");

            Telegram.Bot.Types.User me = await Bot.GetMeAsync();
            Console.Title = me.Username ?? "My awesome Bot";

            using var cts = new CancellationTokenSource();

            // StartReceiving does not block the caller thread. Receiving is done on the ThreadPool.
            ReceiverOptions receiverOptions = new() { AllowedUpdates = { } };
            Bot.StartReceiving(this.HandleUpdateAsync, HandleErrorAsync);

            Console.WriteLine($"Start listening for @{me.Username}");
            Console.ReadLine();

            // Send cancellation request to stop bot
            cts.Cancel();
        }
        public async Task Init(ITelegramBotClient telegramBot, Update update, CancellationToken cancellationToken) 
        {
            if (update.Message == null) return;
            this.ChatId = update.Message.Chat.Id;
            await telegramBot.SendTextMessageAsync(update.Message.Chat.Id, "I will ping you when new post on forum will be published");
        }
        public async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
        {
            var handler = update.Message?.Text switch
            {
                "/rnd@GTONCapitalForumbot" => Init(botClient, update, cancellationToken),
                _ => HandleErrorAsync(botClient, new Exception(), cancellationToken)
            };

            try
            {
                await handler;
            }
            catch (Exception exception)
            {
                await HandleErrorAsync(botClient, exception, cancellationToken);
            }
        }
        public async void OnNewPost(string username, string Message)
        {
            if (this.ChatId != 0) await Bot.SendTextMessageAsync(this.ChatId, $"{username} post new topic: https://forum.gton.capital/t/x/" + Message);
        }
        public async void OnNewComment(string username, string Message, string text) 
        {
            if (this.ChatId == 0) return;
            text = text.Length > 200 ? text.Substring(0, 200) : text;
            await Bot.SendTextMessageAsync(this.ChatId, $"{username} post new comment: {text.Substring(0, 200)} \n https://forum.gton.capital/t/x/" + Message);
        }
        public static Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
        {
            var ErrorMessage = exception switch
            {
                ApiRequestException apiRequestException => $"Telegram API Error:\n[{apiRequestException.ErrorCode}]\n{apiRequestException.Message}",
                _ => exception.ToString()
            };

            Console.WriteLine(ErrorMessage);
            return Task.CompletedTask;
        }
    }
    class DiscordModule 
    {
        private readonly DiscordSocketClient _client;
        private ISocketMessageChannel ChannelID;
        public DiscordModule()
        {
            _client = new DiscordSocketClient();
            _client.Log += LogAsync;
            _client.Ready += ReadyAsync;
            _client.MessageReceived += MessageReceivedAsync;
        }

        public async Task Start()
        {
            var token = "Telegrambot_api_key";
            await _client.LoginAsync(TokenType.Bot, token);
            await _client.StartAsync();
            await Task.Delay(Timeout.Infinite);
        }

        private Task LogAsync(LogMessage log)
        {
            Console.WriteLine(log.ToString());
            return Task.CompletedTask;
        }

        private Task ReadyAsync()
        {
            Console.WriteLine($"{_client.CurrentUser} is connected!");

            return Task.CompletedTask;
        }
        private async Task MessageReceivedAsync(SocketMessage message)
        {
            if (message.Author.Id == _client.CurrentUser.Id)
                return;

            if (message.Content == "!register")
            {
                this.ChannelID = message.Channel;
                await this.ChannelID.SendMessageAsync("I will ping you when new post on forum will be published");
            }
        }
        public async void OnNewPost(string username, string id) 
        {
            if (this.ChannelID != null) 
            {
                await this.ChannelID.SendMessageAsync($"{username} posted new topic: https://forum.graviton.one/t/x/{id}");
            }
        }
        public async void OnNewComment(string username, string id, string text)
        {
            if (this.ChannelID != null)
            {
                text = text.Length > 200 ? text.Substring(0, 200) : text;
                await this.ChannelID.SendMessageAsync($"{username} posted new comment: {text.Substring(0, 200)} \n https://forum.graviton.one/t/x/{id}");
            }
        }
    }
}