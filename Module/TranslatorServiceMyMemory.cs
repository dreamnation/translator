
using OpenSim.Framework;
using OpenSim.Services.Interfaces;

using System;
using System.Collections.Generic;
using System.Text;
using System.Web;

namespace Dreamnation
{
    public class TranslatorServiceMyMemory : ITranslatorService {

        // names come from top of translate.google.com page
        // 2-letter codes come from http://www.loc.gov/standards/iso639-2/php/code_list.php
        // hopefully they apply to mymemory.translate.net as well
        private static string[] allLangCodes = new string[] {
            "af Afrikaans",  "sq Albanian",   "ar Arabic",     "hy Armenian",  "az Azerbaijani", "eu Basque",    "be Belarusian", 
            "bn Bengali",    "bs Bosnian",    "bg Bulgarian",  "ca Catalan",   /*"Cebuano",*/    "ny Chichewa",  "zh Chinese", 
            "hr Croatian",   "cs Czech",      "da Danish",     "nl Dutch",     "en English",     "eo Esperanto", "et Estonian", 
            /*"Filipino",*/  "fi Finnish",    "fr French",     "gl Galician",  "ka Georgian",    "de German",    "el Greek", 
            "gu Gujarati",   "ht Haitian",    "ha Hausa",      "he Hebrew",    "hi Hindi",       /*"Hmong",*/    "hu Hungarian", 
            "is Icelandic",  "ig Igbo",       "id Indonesian", "ga Irish",     "it Italian",     "ja Japanese",  "jv Javanese", 
            "kn Kannada",    "kk Kazakh",     "km Khmer",      "ko Korean",    "lo Lao",         "la Latin",     "lv Latvian", 
            "lt Lithuanian", "mk Macedonian", "mg Malagasy",   "ms Malay",     "ml Malayalam",   "mt Maltese",   "mi Maori", 
            "mr Marathi",    "mn Mongolian",  "my Myanmar",    "ne Nepali",    "no Norwegian",   "fa Persian",   "pl Polish", 
            "pt Portuguese", "pa Punjabi",    "ro Romanian",   "ru Russian",   "sr Serbian",     /*"Sesotho",*/  "si Sinhala", 
            "sk Slovak",     "sl Slovenian",  "so Somali",     "es Spanish",   "su Sundanese",   "sw Swahili",   "sv Swedish", 
            "tg Tajik",      "ta Tamil",      "te Telugu",     "th Thai",      "tr Turkish",     "uk Ukrainian", "ur Urdu", 
            "uz Uzbek",      "vi Vietnamese", "cy Welsh",      "yi Yiddish",   "yo Yoruba",      "zu Zulu"
        };

        public string[] AllLangCodes { get { return allLangCodes; } }

        public string DefLangCode { get { return "en"; } }

        public string Name { get { return "MyMemory"; } }

        private IUserAccountService userAccountService;

        /**
         * @brief Translate a message.
         * http://mymemory.translated.net/doc/spec.php
         */
        public string Translate (IClientAPI client, string srclc, string dstlc, string message)
        {
            if (userAccountService == null) {
                userAccountService = client.Scene.RequestModuleInterface<IUserAccountService> ();
            }
            string email = userAccountService.GetUserAccount (client.Scene.RegionInfo.ScopeID, client.AgentId).Email;
            string query = "q=" + HttpUtility.UrlEncode (message) +
                           "&langpair=" + HttpUtility.UrlEncode (srclc) + "|" + HttpUtility.UrlEncode (dstlc) +
                           "&de=" + HttpUtility.UrlEncode (email);
            string reply = SynchronousHttpRequester.MakeRequest (
                "POST",
                "http://api.mymemory.translated.net/get",
                "application/x-www-form-urlencoded",
                query,
                TranslatorModule.WD_TIMEOUT_MS / 2000,
                null
            );

            int i = reply.IndexOf ("\"translatedText\":\"");
            if (i < 0) throw new ArgumentException ("missing translatedText");
            i += 18;

            StringBuilder sb = new StringBuilder (reply.Length);
            for (; i < reply.Length; i ++) {
                char c = reply[i];
                if (c == '"') break;
                if (c == '\\') {
                    c = reply[++i];
                    if (c == 'u') {
                        c = (char) Convert.ToInt32 (reply.Substring (i + 1, 4), 16);
                        i += 4;
                    }
                }
                sb.Append (c);
            }
            return sb.ToString ();
        }
    }
}
