using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

class Program
{
    static readonly ConcurrentDictionary<string, (WebSocket socket, int colorCode)> users = new();

    static async Task Main()
    {
        string port = Environment.GetEnvironmentVariable("PORT") ?? "5000";
        string url = $"http://+:{port}/send/";

        HttpListener listener = new();
        listener.Prefixes.Add(url);
        listener.Start();

        Console.WriteLine($"Server started");
        while (true)
        {
            var context = await listener.GetContextAsync();

            if (context.Request.IsWebSocketRequest)
                _ = HandleConnectionAsync(context);
            else
            {
                context.Response.StatusCode = 400;
                byte[] buffer = Encoding.UTF8.GetBytes("WebSocket only");
                await context.Response.OutputStream.WriteAsync(buffer);
                context.Response.Close();
            }
        }
    }

    static async Task HandleConnectionAsync(HttpListenerContext context)
    {
        try
        {
            WebSocketContext wsContext = await context.AcceptWebSocketAsync(null);
            var socket = wsContext.WebSocket;

            string nickname = null;
            int colorCode = (int)ConsoleColor.White;

            Console.WriteLine("Client connected");

            byte[] buffer = new byte[1024 * 4];
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                string message = Encoding.UTF8.GetString(buffer, 0, result.Count);

                var parts = message.Split('|', StringSplitOptions.RemoveEmptyEntries);

                if (parts[0] == "REG" && parts.Length == 3)
                {
                    nickname = parts[1];
                    if (!int.TryParse(parts[2], out colorCode))
                        colorCode = (int)ConsoleColor.White;

                    if (users.ContainsKey(nickname))
                    {
                        await SendAsync(socket, "ERR|Nickname is taken already");
                        continue;
                    }

                    users[nickname] = (socket, colorCode);
                    Console.WriteLine(message);
                    await BroadcastAsync($"SYS|{nickname} joined the chat", exclude: nickname);
                }
                else if (parts.Length == 2 && nickname != null)
                {
                    string text = parts[1];
                    string msg = $"{nickname}|{DateTime.Now.ToShortTimeString()}|{colorCode}|{text}";
                    await BroadcastAsync(msg);
                }
                else
                {
                    await SendAsync(socket, "ERR|Incorrect request");
                }
            }

            if (nickname != null)
            {
                users.TryRemove(nickname, out _);
                await BroadcastAsync($"SYS|{nickname} left the chat");
            }

            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed", CancellationToken.None);
            Console.WriteLine($"{nickname ?? "Unknown"} disconnected");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Exception: {ex}");
        }
    }

    static async Task SendAsync(WebSocket socket, string message)
    {
        byte[] buffer = Encoding.UTF8.GetBytes(message);
        await socket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    static async Task BroadcastAsync(string message, string exclude = null)
    {
        byte[] buffer = Encoding.UTF8.GetBytes(message);
        foreach (var user in users)
        {
            if (user.Key == exclude) continue;

            try
            {
                if (user.Value.socket.State == WebSocketState.Open)
                    await user.Value.socket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch
            {
                users.TryRemove(user.Key, out _);
            }
        }
    }
}
