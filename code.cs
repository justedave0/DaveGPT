using System;
using System.IO;
using System.Net;
using System.Text;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Streamer.bot.Common.Events;
using System.Linq;

public class CPHInline
{
    public bool Execute()
    {
    	var input = args["rawInput"].ToString();
    	var user = args.ContainsKey("userName") ? args["userName"].ToString() : "justedave_";
    	var username = CPH.GetGlobalVar<string>("DaveGPT_Username", true);
    	int maxMemoryLength = CPH.GetGlobalVar<int>("DaveGPT_MaxMemoryLength", true);
        
    	if(input.IndexOf(username, StringComparison.OrdinalIgnoreCase) < 0 && input.IndexOf("justeunsimplebot", StringComparison.OrdinalIgnoreCase) < 0)
    	{
            CPH.LogInfo($"Le terme {username} est non retrouvé ou le bot 'justeunsimplebot' n'est pas dans l'input.");
			return true;
    	}
    	
        var memory = GetMemory(maxMemoryLength);
        var gptBehavior = PrePareBehaviorPrompt();
        var exclusionList = GetExclusionList(username);

        if (exclusionList.Any(s => s.Equals(user, StringComparison.OrdinalIgnoreCase)))
        {
            CPH.LogInfo($"User {user} is on the exclusion list. Skipping GPT processing.");
            return false;
        }

        var messageInput = $"Je suis {user} : {input}";
        var response = GenerateResponse(messageInput, gptBehavior, memory);

        if (response != null)
        {
            var cleanedResponse = CleanResponse(response.response);
            if (cleanedResponse.Contains("!"))
            {
                CPH.SendMessage(cleanedResponse.Replace("\"", ""));
            }
            else
            {
                CPH.SendMessage($"@{user} {cleanedResponse}", true);
            }

            UpdateMemory(memory, messageInput, cleanedResponse, maxMemoryLength, response.behavior_change);
        }
        else
        {
            CPH.LogError("Failed to get a valid response from the GPT API.");
        }
        
		CPH.Wait(5000);
		
        return true;
    }

    private List<string> GetExclusionList(string chatbotName)
    {
        return new List<string>
        {
            chatbotName,
            "streamelements",
            "nightbot",
            "streamlabs",
            "pokemoncommunitygame",
            "kofistreambot",
            "fourthwallhq",
            "wizebot"
        };
    }

    private List<Message> GetMemory(int maxMemoryLength)
    {
        try
        {
            var memoryString = CPH.GetGlobalVar<string>("DaveGPT_Memory", true)?.Trim();
            return string.IsNullOrEmpty(memoryString) ? new List<Message>() : JsonConvert.DeserializeObject<List<Message>>(memoryString);
        }
        catch (Exception ex)
        {
            CPH.LogError($"Error reading memory: {ex.Message}");
            return new List<Message>();
        }
    }
    
    private string PrePareBehaviorPrompt()
    {
    	var gptBehavior = CPH.GetGlobalVar<string>("DaveGPT_Behavior", true);
    	var behaviorChange = CPH.GetGlobalVar<string>("DaveGPT_BehaviorChange", true);
    	
    	// Utilisation de Regex pour remplacer le contenu entre les {{ }}
        string pattern = @"\{\{(.*?)\}\}";
        
        return Regex.Replace(gptBehavior, pattern, m => $"{{{{{behaviorChange}}}}}");
    }

    // Helper method to generate response from GPT API
    public ChatGptResponse GenerateResponse(string prompt, string gptBehavior, List<Message> memory)
    {
        var apiKey = CPH.GetGlobalVar<string>("DaveGPT_ApiKey", true);
        var gptModel = CPH.GetGlobalVar<string>("DaveGPT_Model", true);
        var gptTemperature = CPH.GetGlobalVar<string>("DaveGPT_Temperature", true);
        var gptTempValue = Convert.ToDouble(gptTemperature);

        var messages = new List<Message>
        {
            new Message
            {
                role = "system",
                content = gptBehavior
            }
        };

        foreach (var msg in memory)
        {
            messages.Add(new Message { role = msg.role, content = msg.content });
        }

        messages.Add(new Message { role = "user", content = prompt });
        var requestObject = new
        {
            model = gptModel,
            max_tokens = 300,
            temperature = gptTempValue,
            messages = messages
        };

        var requestBody = JsonConvert.SerializeObject(requestObject);
        CPH.LogInfo($"Request Body: {requestBody}");
        try
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create("https://api.openai.com/v1/chat/completions");
            request.Headers.Add("Authorization", "Bearer " + apiKey);
            request.ContentType = "application/json";
            request.Method = "POST";
            var bytes = Encoding.UTF8.GetBytes(requestBody);
            request.ContentLength = bytes.Length;
            using (Stream requestStream = request.GetRequestStream())
            {
                requestStream.Write(bytes, 0, bytes.Length);
            }

            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream responseStream = response.GetResponseStream())
            using (StreamReader reader = new StreamReader(responseStream, Encoding.UTF8))
            {
                var responseBody = reader.ReadToEnd();
                CPH.LogInfo($"Response Body: {responseBody}");
                var root = JsonConvert.DeserializeObject<Root>(responseBody);
                if (root != null && root.choices != null && root.choices.Count > 0)
                {
                    var messageContent = root.choices[0].message.content;
                    
                    string cleanedMessageContent = ExtractJsonFromCodeBlock(messageContent);
                
                    try
					{
						return JsonConvert.DeserializeObject<ChatGptResponse>(cleanedMessageContent);
					}
					catch (JsonException)
					{
						CPH.LogInfo("Response is plain text. Creating a fallback ChatGptResponse object.");
						return new ChatGptResponse
						{
							response = messageContent.Trim(),  
							behavior_change = "no change"    
						};
					}
                }
            }
        }
        catch (WebException webEx)
        {
            using (var response = webEx.Response)
            using (var data = response.GetResponseStream())
            using (var reader = new StreamReader(data))
            {
                string errorText = reader.ReadToEnd();
                CPH.LogError($"API Error Response: {errorText}");
            }
        }
        catch (Exception ex)
        {
            CPH.LogError($"Error during API call: {ex.Message}");
        }

        return null; 
    }

    // Remove the code block markers (```json and ```) and parse the JSON inside
    private string ExtractJsonFromCodeBlock(string content)
    {
        var regex = new Regex(@"```json\s*(\{.*?\})\s*```", RegexOptions.Singleline);
        var match = regex.Match(content);
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        return content;
    }

    // Helper method to clean the response
    private string CleanResponse(string response)
    {
        var cleaned = response.Replace(Environment.NewLine, " ");
        cleaned = Regex.Replace(cleaned, @"\r\n?|\n", " ");
        cleaned = Regex.Replace(cleaned, @"[\r\n]+", " ");
        return Regex.Unescape(cleaned).Trim();
    }

    // Helper method to update memory
    private void UpdateMemory(List<Message> memory, string userMessage, string assistantMessage, int maxMemoryLength, string behavior_change)
    {
        memory.Add(new Message { role = "user", content = userMessage });
        memory.Add(new Message { role = "assistant", content = assistantMessage });

        while (memory.Count > maxMemoryLength)
        {
            memory.RemoveAt(0);
        }

        try
        {
            var memoryString = JsonConvert.SerializeObject(memory);
            CPH.SetGlobalVar("DaveGPT_Memory", memoryString, true);
        }
        catch (Exception ex)
        {
            CPH.LogError($"Error updating memory: {ex.Message}");
        }
        
        CPH.LogDebug($"DaveGPT_BehaviorChange : {behavior_change}");
        CPH.SetGlobalVar("DaveGPT_BehaviorChange", behavior_change, true);
    }
}

// Define necessary classes for the GPT response
public class ChatGptResponse
{
    public string response { get; set; }
    public string behavior_change { get; set; }
}

public class Message
{
    public string role { get; set; }
    public string content { get; set; }
}

public class Choice
{
    public Message message { get; set; }
    public int index { get; set; }
    public object logprobs { get; set; }
    public string finish_reason { get; set; }
}

public class Root
{
    public string id { get; set; }
    public string @object { get; set; }
    public int created { get; set; }
    public string model { get; set; }
    public List<Choice> choices { get; set; }
    public Usage usage { get; set; }
}

public class Usage
{
    public int prompt_tokens { get; set; }
    public int completion_tokens { get; set; }
    public int total_tokens { get; set; }
}