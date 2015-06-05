
using OpenSim.Framework;
using System;
using System.Collections.Generic;
using System.Text;
using System.Web;

namespace Dreamnation
{
    public class TranslatorServiceGoogle : ITranslatorService {

        // names come from top of translate.google.com page
        // 2-letter codes come from http://www.loc.gov/standards/iso639-2/php/code_list.php
        // hopefully they actually match
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

        public string Name { get { return "Google"; } }

        /**
         * @brief Translate a message.
         * https://github.com/Stichoza/google-translate-php/blob/master/src/Stichoza/Google/GoogleTranslate.php
         */
        public string Translate (string agentID, string srclc, string dstlc, string message)
        {
            string query = "client=t&text=" + HttpUtility.UrlEncode (message) +
                           "&hl=en&sl=" + HttpUtility.UrlEncode (srclc) +
                           "&tl=" + HttpUtility.UrlEncode (dstlc) +
                           "&ie=UTF-8&oe=UTF-8&multires=1&otf=1&pc=1&trs=1&ssel=3&tsel=6&sc=1";
            string reply = SynchronousHttpRequester.MakeRequest (
                "POST",
                "http://translate.google.com/translate_a/t",
                "application/x-www-form-urlencoded",
                query,
                TranslatorModule.WD_TIMEOUT_MS / 2000,
                null
            );

            // [[["le chat a écrit «il»","the cat wrote \"it\"","",""]],,"en",,
            // [["le chat",[1],true,false,1000,0,2,0],["a écrit",[2],true,false,957,2,4,0],
            // ["«il»",[3],true,false,904,4,7,0]],[["the cat",1,[["le chat",1000,true,false],
            // ["chat",0,true,false],["du chat",0,true,false],["la chatte",0,true,false],["au chat",0,true,false]],
            // [[0,7]],"the cat wrote \"it\""],["wrote",2,[["a écrit",957,true,false],
            // ["écrit",19,true,false],["écrivit",0,true,false],["écrivait",0,true,false],
            // ["wrote",0,true,false]],[[8,13]],""],["\" it \"",3,[["«il»",904,true,false]],
            // [[14,18]],""]],,,[["en"]],22]

            if (!reply.StartsWith ("[[[\"")) {
                throw new ApplicationException ("bad reply " + reply);
            }
            StringBuilder sb = new StringBuilder (reply.Length);
            for (int i = 4; i < reply.Length; i ++) {
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
