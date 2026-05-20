using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

LoadEnv();

// target twitch channel to monitor
const string ChannelName = "miavoiceteacher";
// const string ChannelName = "TheMainManSWE";
// const string ChannelName = "Jinnytty";

string clientId = Environment.GetEnvironmentVariable("TWITCH_CLIENT_ID") ?? "";
string clientSecret = Environment.GetEnvironmentVariable("TWITCH_CLIENT_SECRET") ?? "";
string initialAccessToken = Environment.GetEnvironmentVariable("TWITCH_ACCESS_TOKEN ") ?? "";

if (clientId == "" || clientSecret == "")
{
  Console.WriteLine("Missing TWITCH_CLIENT_ID or TWITCH_CLIENT_SECRET in MiaStreamTmestamp/.env");
  return;
}

using HttpClient twitch = new() { Timeout = TimeSpan.FromSeconds(10) };
using HttpClient twitchAuth = new() { Timeout = TimeSpan.FromSeconds(10) };
TwitchAccessTokenProvider tokenProvider = new(twitchAuth, clientId, clientSecret, initialAccessToken);

twitch.DefaultRequestHeaders.Add("Client-ID", clientId);

PrintStatus(await CheckStreamAsync(twitch, tokenProvider));

int grabbedCount = 0;

while (true)
{
  Console.Write("> ");
  ConsoleKeyInfo key = Console.ReadKey(intercept: true);
  Console.WriteLine(key.Key == ConsoleKey.Enter ? "" : key.KeyChar);

  if (key.Key == ConsoleKey.Q)
  {
    break;
  }

  StreamCheck check = await CheckStreamAsync(twitch, tokenProvider);

  if (check.Status != StreamStatus.Live)
  {
    PrintStatus(check);
    continue;
  }

  TimeSpan timestamp = check.Timestamp!.Value;

  string timestampText = FormatTimestamp(check.Timestamp!.Value);

  string vodLink = check.VodId == "" ? "" : BuildVodLink(check.VodId, timestamp);

  string toCopy = vodLink == ""
      ? $"{timestampText} from today's stream"
      : $"{vodLink} ({timestampText}) from today's stream";

  grabbedCount++;

  Console.WriteLine(CopyToClipboard(toCopy)
    ? $"{grabbedCount}) TIMESTAMP GRABBED (Ctrl+V to paste anywhere): {vodLink} <-> {timestampText}"
    : $"{grabbedCount}) FAILED TO COPY... Desired text: {toCopy}");

  Console.WriteLine("Press any key except Q to copy the next timestamp.");
}

Console.WriteLine("Closed.");

async Task<StreamCheck> CheckStreamAsync(HttpClient client, TwitchAccessTokenProvider tokenProvider)
{
  string url = $"https://api.twitch.tv/helix/streams?user_login={ChannelName}";

  try
  {
    TokenRefreshResult token = await tokenProvider.ApplyAuthorizationAsync(client);

    if (!token.Success)
    {
      return StreamCheck.Error(token.Message);
    }

    using HttpResponseMessage response = await client.GetAsync(url);

    if (!response.IsSuccessStatusCode)
    {
      if (response.StatusCode == HttpStatusCode.Unauthorized)
      {
        TokenRefreshResult refreshedToken = await tokenProvider.ApplyAuthorizationAsync(client, forceRefresh: true);

        if (!refreshedToken.Success)
        {
          return StreamCheck.Error(refreshedToken.Message);
        }

        using HttpResponseMessage retryResponse = await client.GetAsync(url);

        if (retryResponse.IsSuccessStatusCode)
        {
          return await ReadStreamCheckAsync(client, retryResponse);
        }

        return StreamCheck.Error(GetTwitchErrorMessage(retryResponse));
      }

      return StreamCheck.Error(GetTwitchErrorMessage(response));
    }

    return await ReadStreamCheckAsync(client, response);
  }
  catch (HttpRequestException error)
  {
    return StreamCheck.Error($"Could not reach Twitch. {error.Message}");
  }
  catch (TaskCanceledException)
  {
    return StreamCheck.Error("Twitch request timed out.");
  }
  catch (JsonException)
  {
    return StreamCheck.Error("Twitch returned malformed JSON.");
  }
  catch (Exception error)
  {
    return StreamCheck.Error($"Unexpected app error: {error.Message}");
  }
}

static async Task<StreamCheck> ReadStreamCheckAsync(
    HttpClient client,
    HttpResponseMessage response)
{
  using Stream stream = await response.Content.ReadAsStreamAsync();
  using JsonDocument json = await JsonDocument.ParseAsync(stream);

  if (!json.RootElement.TryGetProperty("data", out JsonElement data) ||
      data.ValueKind != JsonValueKind.Array)
  {
    return StreamCheck.Error("Twitch returned a response this app did not understand.");
  }

  if (data.GetArrayLength() == 0)
  {
    return StreamCheck.Offline();
  }

  if (!data[0].TryGetProperty("started_at", out JsonElement startedAtJson) ||
      !DateTimeOffset.TryParse(startedAtJson.GetString(), out DateTimeOffset startedAt))
  {
    return StreamCheck.Error("Twitch did not include the stream start time.");
  }

  if (!data[0].TryGetProperty("user_id", out JsonElement userIdJson))
  {
    return StreamCheck.Error("Twitch did not include the user ID.");
  }

  string? userId = userIdJson.GetString();

  if (string.IsNullOrWhiteSpace(userId))
  {
    return StreamCheck.Error("Twitch user ID was empty.");
  }

  string vodId = await GetLatestVodIdAsync(client, userId);

  return StreamCheck.Live(
      DateTimeOffset.UtcNow - startedAt,
      userId,
      vodId
  );
}

static async Task<string> GetLatestVodIdAsync(HttpClient client, string userId)
{
  string url =
      $"https://api.twitch.tv/helix/videos?user_id={userId}&type=archive&first=1";

  using HttpResponseMessage response = await client.GetAsync(url);

  if (!response.IsSuccessStatusCode)
  {
    return "";
  }

  using Stream stream = await response.Content.ReadAsStreamAsync();
  using JsonDocument json = await JsonDocument.ParseAsync(stream);

  if (!json.RootElement.TryGetProperty("data", out JsonElement data) ||
      data.ValueKind != JsonValueKind.Array ||
      data.GetArrayLength() == 0)
  {
    return "";
  }

  if (!data[0].TryGetProperty("id", out JsonElement vodIdJson))
  {
    return "";
  }

  return vodIdJson.GetString() ?? "";
}

static string GetTwitchErrorMessage(HttpResponseMessage response)
{
  return response.StatusCode switch
  {
    HttpStatusCode.Unauthorized => "Twitch rejected the access token. The app tried to refresh it; check TWITCH_CLIENT_ID and TWITCH_CLIENT_SECRET in .env.",
    HttpStatusCode.Forbidden => "Twitch rejected the client/token permissions. Check your Twitch app credentials.",
    HttpStatusCode.TooManyRequests => "Twitch rate limited the app. Wait a bit, then try again.",
    HttpStatusCode.InternalServerError or
    HttpStatusCode.BadGateway or
    HttpStatusCode.ServiceUnavailable or
    HttpStatusCode.GatewayTimeout => $"Twitch is having server trouble: {(int)response.StatusCode} {response.ReasonPhrase}.",
    _ => $"Twitch returned {(int)response.StatusCode} {response.ReasonPhrase}."
  };
}

static void PrintStatus(StreamCheck check)
{
  Console.ForegroundColor = check.Status switch
  {
    StreamStatus.Live => ConsoleColor.Green,
    StreamStatus.Offline => ConsoleColor.Red,
    _ => ConsoleColor.Yellow
  };

  string petName = ChannelName switch
  {
    "miavoiceteacher" => "Mia",
    _ => ChannelName
  };
  Console.WriteLine(check.Status switch
  {
    StreamStatus.Live => $"[LIVE] {petName} is live",
    StreamStatus.Offline => $"[OFFLINE] {petName} is not streaming",
    _ => "[ERROR] Could not check Twitch"
  });

  Console.ResetColor();

  if (check.Status == StreamStatus.Error)
  {
    Console.WriteLine(check.Message);
    Console.WriteLine("Press Enter to try again. Press Q to exit.");
    return;
  }

  Console.WriteLine(check.Status == StreamStatus.Live
      ? "Press any key except Q to copy the stream timestamp. Press Q to exit."
      : "Press Enter to check again. Press Q to exit.");
}

static string FormatTimestamp(TimeSpan value)
{
  int hours = (int)value.TotalHours;
  return hours > 0
      ? $"{hours}:{value.Minutes:00}:{value.Seconds:00}"
      : $"{value.Minutes:00}:{value.Seconds:00}";
}

static string BuildVodLink(string vodId, TimeSpan timestamp)
{
  int hours = (int)timestamp.TotalHours;

  return
      $"https://www.twitch.tv/videos/{vodId}?t={hours}h{timestamp.Minutes}m{timestamp.Seconds}s";
}

static bool CopyToClipboard(string text)
{
  try
  {
    using Process process = Process.Start(new ProcessStartInfo
    {
      FileName = "clip.exe",
      RedirectStandardInput = true,
      UseShellExecute = false,
      CreateNoWindow = true
    })!;

    process.StandardInput.Write(text);
    process.StandardInput.Close();
    process.WaitForExit();

    return process.ExitCode == 0;
  }
  catch
  {
    return false;
  }
}

static void LoadEnv()
{
  string envPath = Path.Combine(AppContext.BaseDirectory, ".env");

  if (!File.Exists(envPath))
  {
    return;
  }

  foreach (string line in File.ReadAllLines(envPath))
  {
    string trimmed = line.Trim();

    if (trimmed == "" || trimmed.StartsWith('#'))
    {
      continue;
    }

    string[] parts = trimmed.Split('=', 2);

    if (parts.Length == 2)
    {
      Environment.SetEnvironmentVariable(
          parts[0].Trim(),
          parts[1].Trim().Trim('"')
      );
    }
  }
}

enum StreamStatus
{
  Live,
  Offline,
  Error
}

sealed record StreamCheck(
    StreamStatus Status,
    TimeSpan? Timestamp = null,
    string Message = "",
    string UserId = "",
    string VodId = ""
)
{
  public static StreamCheck Live(TimeSpan timestamp, string userId, string vodId)
      => new(StreamStatus.Live, timestamp, "", userId, vodId);

  public static StreamCheck Offline()
      => new(StreamStatus.Offline);

  public static StreamCheck Error(string message)
      => new(StreamStatus.Error, null, message);
}

sealed class TwitchAccessTokenProvider
{
  private static readonly TimeSpan RefreshWindow = TimeSpan.FromMinutes(5);
  private readonly HttpClient client;
  private readonly string clientId;
  private readonly string clientSecret;
  private string accessToken;
  private DateTimeOffset expiresAt = DateTimeOffset.MinValue;

  public TwitchAccessTokenProvider(HttpClient client, string clientId, string clientSecret, string accessToken)
  {
    this.client = client;
    this.clientId = clientId;
    this.clientSecret = clientSecret;
    this.accessToken = accessToken;
  }

  public async Task<TokenRefreshResult> ApplyAuthorizationAsync(HttpClient twitch, bool forceRefresh = false)
  {
    TokenRefreshResult result = await EnsureValidTokenAsync(forceRefresh);

    if (result.Success)
    {
      twitch.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    }

    return result;
  }

  private async Task<TokenRefreshResult> EnsureValidTokenAsync(bool forceRefresh)
  {
    if (!forceRefresh &&
        accessToken != "" &&
        expiresAt > DateTimeOffset.UtcNow.Add(RefreshWindow))
    {
      return TokenRefreshResult.Ok();
    }

    return await RefreshTokenAsync();
  }

  private async Task<TokenRefreshResult> RefreshTokenAsync()
  {
    using FormUrlEncodedContent content = new(new Dictionary<string, string>
    {
      ["client_id"] = clientId,
      ["client_secret"] = clientSecret,
      ["grant_type"] = "client_credentials"
    });

    try
    {
      using HttpResponseMessage response = await client.PostAsync("https://id.twitch.tv/oauth2/token", content);
      using Stream stream = await response.Content.ReadAsStreamAsync();
      using JsonDocument json = await JsonDocument.ParseAsync(stream);

      if (!response.IsSuccessStatusCode)
      {
        string message = json.RootElement.TryGetProperty("message", out JsonElement errorMessage)
            ? errorMessage.GetString() ?? response.ReasonPhrase ?? "Unknown error"
            : response.ReasonPhrase ?? "Unknown error";

        return TokenRefreshResult.Error($"Could not refresh Twitch access token: {(int)response.StatusCode} {message}");
      }

      if (!json.RootElement.TryGetProperty("access_token", out JsonElement tokenJson) ||
          !json.RootElement.TryGetProperty("expires_in", out JsonElement expiresInJson))
      {
        return TokenRefreshResult.Error("Twitch token refresh response did not include an access token or expiry.");
      }

      string? newAccessToken = tokenJson.GetString();

      if (string.IsNullOrWhiteSpace(newAccessToken) || !expiresInJson.TryGetInt32(out int expiresInSeconds))
      {
        return TokenRefreshResult.Error("Twitch token refresh response was malformed.");
      }

      accessToken = newAccessToken;
      expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds);

      return TokenRefreshResult.Ok();
    }
    catch (HttpRequestException error)
    {
      return TokenRefreshResult.Error($"Could not reach Twitch auth. {error.Message}");
    }
    catch (TaskCanceledException)
    {
      return TokenRefreshResult.Error("Twitch auth request timed out.");
    }
    catch (JsonException)
    {
      return TokenRefreshResult.Error("Twitch auth returned malformed JSON.");
    }
  }
}

sealed record TokenRefreshResult(bool Success, string Message = "")
{
  public static TokenRefreshResult Ok() => new(true);
  public static TokenRefreshResult Error(string message) => new(false, message);
}

