
using OpenSim.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Web;

namespace Dreamnation
{
    public class TranslatorServiceGoogle : ITranslatorService {
        private string apikey = null;

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
         *
         * ref: https://cloud.google.com/translate/v2/using_rest#auth
         *
         *   https://www.googleapis.com/language/translate/v2?key=xxxxx&source=en&target=de&q=Hello%20world
         */
        public string Translate (string agentID, string srclc, string dstlc, string message)
        {
            if (apikey == null) {
                StreamReader keyreader = new StreamReader ("translateapikey.txt");
                apikey = keyreader.ReadLine ();
                keyreader.Close ();
            }

            string query = "key=" + HttpUtility.UrlEncode (apikey) +
                           "&q="  + HttpUtility.UrlEncode (message) +
                           "&source=" + HttpUtility.UrlEncode (srclc) +
                           "&target=" + HttpUtility.UrlEncode (dstlc);

            WebRequest request = WebRequest.Create ("https://www.googleapis.com/language/translate/v2?" + query);
            request.Method     = "GET";
            request.Timeout    = TranslatorModule.WD_TIMEOUT_MS / 2;
            string reply       = new StreamReader (request.GetResponse ().GetResponseStream ()).ReadToEnd ();

            // {
            //    "data": {
            //        "translations": [
            //            {
            //                "translatedText": "Hallo Welt"
            //            }
            //        ]
            //    }
            // }

            Dictionary<object[],object> parsed = ParseJSON (reply);
            string translation = (string) parsed[new object[] { "data", "translations", 0, "translatedText" }];

            StreamWriter logwriter = File.AppendText ("translateusage.log");
            try {
                string now = DateTime.UtcNow.ToString ("u");
                logwriter.Write (agentID + " " + now + " " + message.Length + " " + srclc + "->" + dstlc + "\n");
            } finally {
                logwriter.Close ();
            }

            return translation;
        }

        /** Borrowed from XMRInstAbstract.cx **/

        private static Dictionary<object[],object> ParseJSON (string json)
        {
            Dictionary<object[],object> dict = new Dictionary<object[],object> (new ObjectArrayComparer ());
            int idx = ParseJSON (dict, new object[0], json, 0);
            while (idx < json.Length) {
                if (json[idx] > ' ') throw new Exception ("left-over json " + json.Substring (idx));
                idx ++;
            }
            return dict;
        }

        private static int ParseJSON (Dictionary<object[],object> dict, object[] keys, string json, int idx)
        {
            char c;

            while ((c = json[idx++]) <= ' ') { }
            switch (c) {

                // '{' <keystring> ':' <value> [ ',' <keystring> ':' <value> ... ] '}'
                case '{': {
                    do {
                        string key = ParseJSONString (json, ref idx);
                        while ((c = json[idx++]) <= ' ') { }
                        if (c != ':') throw new Exception ("missing : after key");
                        idx = ParseJSON (dict, ParseJSONKeyAdd (keys, key), json, idx);
                        while ((c = json[idx++]) <= ' ') { }
                    } while (c == ',');
                    if (c != '}') throw new Exception ("missing , or } after value");
                    break;
                }

                // '[' <value> [ ',' <value> ... ] ']'
                case '[': {
                    int index = 0;
                    do {
                        object key = index ++;
                        idx = ParseJSON (dict, ParseJSONKeyAdd (keys, key), json, idx);
                        while ((c = json[idx++]) <= ' ') { }
                    } while (c == ',');
                    if (c != ']') throw new Exception ("missing , or ] after value");
                    break;
                }

                // '"'<string>'"'
                case '"': {
                    -- idx;
                    string val = ParseJSONString (json, ref idx);
                    dict.Add (keys, val);
                    break;
                }

                // true false null
                case 't': {
                    if (json.Substring (idx, 3) != "rue") throw new Exception ("bad true in json");
                    idx += 3;
                    dict.Add (keys, 1);
                    break;
                }

                case 'f': {
                    if (json.Substring (idx, 4) != "alse") throw new Exception ("bad false in json");
                    idx += 4;
                    dict.Add (keys, 0);
                    break;
                }

                case 'n': {
                    if (json.Substring (idx, 3) != "ull") throw new Exception ("bad null in json");
                    idx += 3;
                    dict.Add (keys, null);
                    break;
                }

                // otherwise assume it's a number
                default: {
                    -- idx;
                    object val = ParseJSONNumber (json, ref idx);
                    dict.Add (keys, val);
                    break;
                }
            }

            return idx;
        }

        // Given the key for a whole array, create a key for a given element of the array
        private static object[] ParseJSONKeyAdd (object[] oldkeys, object key)
        {
            int oldkeyslen = oldkeys.Length;
            object[] array = oldkeys;
            Array.Resize<object> (ref array, oldkeyslen + 1);
            array[oldkeyslen] = key;
            return array;
        }

        // Parse out a JSON string
        private static string ParseJSONString (string json, ref int idx)
        {
            char c;

            while ((c = json[idx++]) <= ' ') { }
            if (c != '"') throw new Exception ("bad start of json string");

            StringBuilder sb = new StringBuilder ();
            while ((c = json[idx++]) != '"') {
                if (c == '\\') {
                    c = json[idx++];
                    switch (c) {
                        case 'b': {
                            c = '\b';
                            break;
                        }
                        case 'f': {
                            c = '\f';
                            break;
                        }
                        case 'n': {
                            c = '\n';
                            break;
                        }
                        case 'r': {
                            c = '\r';
                            break;
                        }
                        case 't': {
                            c = '\t';
                            break;
                        }
                        case 'u': {
                            c = (char) Int32.Parse (json.Substring (idx, 4), 
                                                    System.Globalization.NumberStyles.HexNumber);
                            idx += 4;
                            break;
                        }
                        default: break;
                    }
                }
                sb.Append (c);
            }
            return sb.ToString ();
        }

        // Parse out a JSON number
        private static object ParseJSONNumber (string json, ref int idx)
        {
            char c;

            while ((c = json[idx++]) <= ' ') { }

            bool expneg = false;
            bool isneg  = false;
            int decpt   = -1;
            int expon   = 0;
            int ival    = 0;
            double dval = 0;

            if (c == '-') {
                isneg = true;
                c = json[idx++];
            }
            if ((c < '0') || (c > '9')) {
                throw new Exception ("bad json number");
            }
            while ((c >= '0') && (c <= '9')) {
                dval *= 10;
                ival *= 10;
                dval += c - '0';
                ival += c - '0';
                c = '\0';
                if (idx < json.Length) c = json[idx++];
            }
            if (c == '.') {
                decpt = 0;
                c = '\0';
                if (idx < json.Length) c = json[idx++];
                while ((c >= '0') && (c <= '9')) {
                    dval *= 10;
                    dval += c - '0';
                    decpt ++;
                    c = '\0';
                    if (idx < json.Length) c = json[idx++];
                }
            }
            if ((c == 'e') || (c == 'E')) {
                if (decpt < 0) decpt = 0;
                c = json[idx++];
                if (c == '-') expneg = true;
                if ((c == '-') || (c == '+')) c = json[idx++];
                while ((c >= '0') && (c <= '9')) {
                    expon *= 10;
                    expon += c - '0';
                    c = '\0';
                    if (idx < json.Length) c = json[idx++];
                }
                if (expneg) expon = -expon;
            }

            if (c != 0) -- idx;
            if (decpt < 0) {
                if (isneg) ival = -ival;
                return ival;
            } else {
                if (isneg) dval = -dval;
                dval *= Math.Pow (10, expon - decpt);
                return dval;
            }
        }

        private class ObjectArrayComparer : EqualityComparer<object[]> {
            public override bool Equals (object[] a, object[] b)
            {
                int len = a.Length;
                if (len != b.Length) return false;
                for (int i = 0; i < len; i ++) {
                    if (!a[i].Equals (b[i])) return false;
                }
                return true;
            }
            public override int GetHashCode (object[] a)
            {
                int len = a.Length;
                int hash = len;
                for (int i = 0; i < len; i ++) {
                    hash ^= a[i].GetHashCode ();
                }
                return hash;
            }
        }
    }
}
