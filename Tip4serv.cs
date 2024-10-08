/***********************************************************************************************************************/
/*** DO NOT edit this file! Edit the files under `oxide/config` and/or `oxide/lang`, created once plugin has loaded. ***/
/***********************************************************************************************************************/

using Oxide.Core.Libraries.Covalence;
using Oxide.Core;
using Oxide.Core.Libraries;
using System.Text;
using System.Security.Cryptography;
using System.Buffers;
using System.Globalization;

namespace Oxide.Plugins
{
    [Info("Tip4serv", "Murgator & Duster", "1.4.6")]
    [Description("Allows Admin to monetize their 7 Days to die & Rust server from their Tip4serv store")]
    public class Tip4serv : CovalencePlugin
    {
        private class PluginConfig
        {
            public int request_interval_in_minutes;
            public string configkey;
            public string order_received_text;
        }
        [Serializable]
        public class ResponseData
        {
            public string date;
            public string action;
            public Dictionary<int, int> cmds;
            public int status;
            public string username;
        }
        [Serializable]
        public class Payments
        {
            public string player;
            public string action;
            public string id;
            public string steamid;
            public PaymentCmd[] cmds;
        }
        [Serializable]
        public class PaymentCmd
        {
            public string str;
            public int id;
            public int state;
        }
        private String key_msg = "Please set the config key to a valid key in your config/Tip4Serv.json file. Make sure you have copied the entire key on Tip4Serv.com (Ctrl+A then CTRL+C)";
        private bool Stopped = false;
        private Timer PaymentTimer;
        private PluginConfig config;
        protected override void LoadDefaultConfig()
        {
            LogWarning("Creating a new configuration file");
            Config.WriteObject(GetDefaultConfig(), true);
        }
        private PluginConfig GetDefaultConfig()
        {
            return new PluginConfig
            {
                request_interval_in_minutes = 1,
                configkey = "YOUR_CONFIG_KEY",
                order_received_text = "[#cyan][Tip4serv][/#] You have received your order. Thank you !"
            };
        }
        private Dictionary<String,IPlayer> GetPlayers() {
            IEnumerable<IPlayer> players = covalence.Players.Connected;
             Dictionary<String,IPlayer> tip4customers = new Dictionary<string,IPlayer>();
            foreach (IPlayer player in players)
            {
                tip4customers[player.Id] = player;
            }
           return tip4customers;
        }
        private void Loaded()
        {            
            #if !SEVENDAYSTODIE && !RUST
               LogError("This plugin only works for the 7 Days to Die or Rust Game");
               Stopped = true;
            #else
               Tip4Print("Tip4serv plugin has started");
               config = Config.ReadObject<PluginConfig>();
            #endif
        }
        private void Unload()
        {
            key_msg = null;
            if(PaymentTimer != null && !PaymentTimer.Destroyed)
                PaymentTimer.Destroy();
        }
        void OnServerInitialized()
        {
            if (!Stopped)
            {
                //check Tip4serv connection on script start
                string[] key_part = config.configkey.Split('.');
                if (key_part.Length != 3)
                {
                    Tip4Print(key_msg);
                    return;
                }
                check_pending_commands(config.configkey, "no");
                PaymentTimer = timer.In((float)config.request_interval_in_minutes * 60f,() => PaymentChecker());
            }
        }
        private void PaymentChecker()
        {
            string[] key_part = config.configkey.Split('.');
            if (key_part.Length != 3)
            {
                Tip4Print(key_msg);
                return;
            }
            check_pending_commands(config.configkey, "yes");
            if(PaymentTimer!=null && !PaymentTimer.Destroyed)
                PaymentTimer.Destroy();
            PaymentTimer = timer.In((float)config.request_interval_in_minutes * 60f,() => PaymentChecker());
        }
        private void Tip4Print(string content)
        {
            LogWarning(content);
        }
        private void check_pending_commands(string key, string get_cmd)
        {
            DateTimeOffset timestamp = DateTimeOffset.UtcNow;
            //HMAC calculation
            string HMAC = calculateHMAC(key, timestamp, out int firstPeriodIndex);
            //get last infos from the json file
            Dictionary<string, ResponseData> response = LoadFile("tip4serv_response");
            string json_encoded = "";
            if (response.Count > 0)
            {
                json_encoded = System.Uri.EscapeDataString(Utility.ConvertToJson(response));
            }
            //request tip4serv
            string statusUrl = $"https://api.tip4serv.com/payments_api_v2.php?id={key[..firstPeriodIndex]}&time={timestamp.ToUnixTimeSeconds()}&json={json_encoded}&get_cmd={get_cmd}";
            Dictionary<string, string> Headers = new Dictionary<string, string> { { "Authorization", HMAC } };
            webrequest.Enqueue(statusUrl, null, (code, HTTPresponse) => {

                if (code != 200 || HTTPresponse == null)
                {
                    if (get_cmd == "no")
                    {
                        Tip4Print("Tip4serv API is temporarily unavailable, maybe you are making too many requests. Please try again later");
                    }
                    return;
                }
                //tip4serv connect
                if (get_cmd == "no")
                {
                    Tip4Print(HTTPresponse);
                    return;
                }
                response.Clear();
                //check for errors
                if (HTTPresponse.Contains("No pending payments found"))
                {
                    Interface.Oxide.DataFileSystem.WriteObject("tip4serv_response", response);
                    return;
                }
                else if (HTTPresponse.StartsWith("\"[Tip4serv "))
                {
                    Tip4Print(HTTPresponse);
                    return;
                }                
                //clear old json infos
                Interface.Oxide.DataFileSystem.WriteObject("tip4serv_response", response);
                Dictionary<String,IPlayer> players = GetPlayers();
                var json_decoded = Utility.ConvertFromJson<List<Payments>>(HTTPresponse);
                //loop customers
                for (int i = 0; i < json_decoded.Count; i++)
                {
                    ResponseData new_obj = new ResponseData();
                    Dictionary<int, int> new_cmds = new Dictionary<int, int>();
                    string payment_id = json_decoded[i].id;
                    new_obj.date = DateTime.Now.ToString();
                    new_obj.action = json_decoded[i].action;
                    new_obj.username = "";
                    //check if player is online
                    IPlayer player_infos = checkifPlayerIsLoaded(json_decoded[i].steamid,players);
                    if (player_infos != null)
                    {
                        new_obj.username = player_infos.Name;
                        player_infos.Message(config.order_received_text);
                    }
                    if (json_decoded[i].cmds.Length != 0)
                    {
                        for (int j = 0; j < json_decoded[i].cmds.Length; j++)
                        {
                            //do not run this command if the player must be online
                            if (player_infos == null && (json_decoded[i].cmds[j].str.Contains("{") || (json_decoded[i].cmds[j].state == 1)))
                            {
      
                                new_obj.status = 14;
                            }
                            else
                            {                                
                                #if SEVENDAYSTODIE
                                if (json_decoded[i].cmds[j].str.Contains("{7dtd_username}"))
                                #elif RUST
                                if (json_decoded[i].cmds[j].str.Contains("{rust_username}"))
                                #endif
                                {
                                    if (player_infos != null) {
                                        #if SEVENDAYSTODIE
                                        json_decoded[i].cmds[j].str = json_decoded[i].cmds[j].str.Replace("{7dtd_username}", player_infos.Name);
                                        #elif RUST 
                                        json_decoded[i].cmds[j].str = json_decoded[i].cmds[j].str.Replace("{rust_username}", player_infos.Name);
                                        #endif                                       
                                    }
                                }
                                string[] empty = { };
                                exe_command(json_decoded[i].cmds[j].str, empty);
                                new_cmds[json_decoded[i].cmds[j].id] = 3;
                            }
                        }
                        new_obj.cmds = new_cmds;
                        if (new_obj.status == 0)
                        {
                            new_obj.status = 3;
                        }
                        response[payment_id] = new_obj;
                    }
                }
                //save the new json file
                Interface.Oxide.DataFileSystem.WriteObject("tip4serv_response", response);

            }, this, RequestMethod.GET, Headers, 30f);
        }
        private Dictionary<string, ResponseData> LoadFile(string path)
        {
            Dictionary<string, ResponseData> response;
            try
            {
                response = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, ResponseData>>("tip4serv_response");
                if (response == null)
                {
                    response = new Dictionary<string, ResponseData>();
                }
            }
            catch (Exception)
            {
                response = new Dictionary<string, ResponseData>();
            }
            return response;
        }

        // The key format is `<server id>.<32 byte key>.<128 byte private secret>`
        private static string calculateHMAC(string key, DateTimeOffset timestamp, out int firstPeriod)
        {
            // Use an array pool for buffer allocation
            byte[] keyBytes = ArrayPool<byte>.Shared.Rent(key.Length);

            // Convert the key to ASCII bytes
            Encoding.ASCII.GetBytes(key, keyBytes);

            // Remember that the ArrayPool returns a buffer with
            // a minimum length of key.Length, so we need to trim it.
            Span<byte> keyBytesSpan = keyBytes.AsSpan(0, key.Length);

            // Find the indexes of the periods in the key
            firstPeriod = key.IndexOf('.');
            int secondPeriod = key.IndexOf('.', firstPeriod + 1);

            // Copy the third part of the key to the second part of the key
            // Similar to `parts[1] = parts[2]`
            int endingIndex = key.Length - secondPeriod - 1 + firstPeriod;
            keyBytesSpan[(secondPeriod + 1)..].CopyTo(keyBytesSpan[firstPeriod..]);

            // Append the timestamp to the key
            endingIndex += Encoding.ASCII.GetBytes(timestamp.ToUnixTimeSeconds().ToString(CultureInfo.CurrentCulture), keyBytesSpan[endingIndex..]);

            // Create a buffer for the signature
            Span<byte> signature = stackalloc byte[HMACSHA256.HashSizeInBytes];
            HMACSHA256.HashData(keyBytesSpan[(firstPeriod + 1)..secondPeriod], keyBytesSpan[..endingIndex], signature);

            // Convert the bytes to hex
            for (int i = 0; i < HMACSHA256.HashSizeInBytes; i++)
            {
                if (!signature[i].TryFormat(keyBytesSpan[(i * 2)..], out _, "x2", CultureInfo.CurrentCulture))
                {
                    // Return the array back to the array pool
                    ArrayPool<byte>.Shared.Return(keyBytes);
                    throw new CryptographicException("Failed to format the hash from ASCII bytes to hex bytes.");
                }
            }

            // Convert the buffer to a base64 string, but only the part that contains the signature
            string result = Convert.ToBase64String(keyBytesSpan[..(HMACSHA256.HashSizeInBytes * 2)]);

            // Return the array back to the array pool
            ArrayPool<byte>.Shared.Return(keyBytes);
            return result;
        }
        private IPlayer checkifPlayerIsLoaded(string steam_id, Dictionary<string,IPlayer> players)
        {
 
            IPlayer SteamPlayer = players.TryGetValue(steam_id, out SteamPlayer) ? SteamPlayer : null;     
            if (SteamPlayer == null)
            {
                return null;
            }
            #if RUST
            if (SteamPlayer.IsConnected)
            {
                return SteamPlayer;
            }
            else
            {
                return null;
            }
            #elif SEVENDAYSTODIE
            try {
                //here is the trick we get the position
                //if the position of the player is returned it mean the player is connected
                //on the other case if Position() throw an exception it means that the player ain't connected 
                GenericPosition JUNKPOS = SteamPlayer.Position(); 
                return SteamPlayer;
            } catch(KeyNotFoundException e) {
                return null;
            }
            #endif
            return null;
        }
        private void exe_command(string cmd, string[] CmdArgs)
        {
            Tip4Print("Tip4serv execute command: "+cmd);
            server.Command(cmd, CmdArgs);
        }
    }
}
