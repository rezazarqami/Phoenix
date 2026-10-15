using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Phoenix.Web;

/// <summary>Keep expanded evidence readable without exceeding photo caption limits.</summary>
internal static class TelegramPhotoDelivery
{
    internal static async Task<HttpResponseMessage> SendAsync(HttpClient client, string photoUrl,
        MultipartFormDataContent original, CancellationToken token)
    {
        string Name(HttpContent part) => part.Headers.ContentDisposition?.Name?.Trim('"') ?? "";
        var captionPart=original.FirstOrDefault(x=>Name(x)=="caption");
        var caption=captionPart is null ? "" : await captionPart.ReadAsStringAsync(token);
        if(caption.Length<=1000)return await client.PostAsync(photoUrl,original,token);
        var chat=await original.Single(x=>Name(x)=="chat_id").ReadAsStringAsync(token);
        var markupPart=original.FirstOrDefault(x=>Name(x)=="reply_markup");
        var markup=markupPart is null ? null : await markupPart.ReadAsStringAsync(token);
        using var photo=new MultipartFormDataContent();
        foreach(var part in original.Where(x=>Name(x) is not ("caption" or "reply_markup" or "parse_mode")))photo.Add(part);
        photo.Add(new StringContent("گزارش کامل این نمودار در پیام بعدی آمده است."),"caption");
        var response=await client.PostAsync(photoUrl,photo,token);
        if(!response.IsSuccessStatusCode)return response;
        int? messageId=null;
        try
        {
            using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            if(json.RootElement.TryGetProperty("result",out var result)&&result.TryGetProperty("message_id",out var id))messageId=id.GetInt32();
        }
        catch(JsonException) { }
        response.Dispose();
        var chunks=PlainChunks(caption);
        for(var i=0;i<chunks.Count;i++)
        {
            var fields=new Dictionary<string,string>{{"chat_id",chat},{"text",chunks[i]}};
            if(messageId.HasValue)fields["reply_parameters"]=JsonSerializer.Serialize(new { message_id=messageId.Value });
            if(i==chunks.Count-1 && markup is not null)fields["reply_markup"]=markup;
            using var body=new FormUrlEncodedContent(fields);
            response=await client.PostAsync(photoUrl[..^"sendPhoto".Length]+"sendMessage",body,token);
            if(!response.IsSuccessStatusCode || i==chunks.Count-1)return response;
            response.Dispose();
        }
        throw new InvalidOperationException("Missing photo evidence message.");
    }
    internal static IReadOnlyList<string> PlainChunks(string html)
    {
        var plain=WebUtility.HtmlDecode(Regex.Replace(html,"<[^>]+>",""));
        var chunks=new List<string>();
        while(plain.Length>3500)
        {
            var end=plain.LastIndexOf('\n',3499,3500);
            if(end<1000)end=char.IsHighSurrogate(plain[3499])?3499:3500;
            else end++;
            chunks.Add(plain[..end]);plain=plain[end..];
        }
        if(plain.Length>0)chunks.Add(plain);
        return chunks.Count>0 ? chunks : ["گزارش بدون متن"];
    }
}
