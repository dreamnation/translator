
using log4net;
using Nini.Config;
using Mono.Addins;

using OpenMetaverse;

using OpenSim.Framework;
using OpenSim.Framework.Client;
using OpenSim.Framework.Monitoring;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Web;

[assembly: Addin ("TranslatorModule", "0.1")]
[assembly: AddinDependency ("OpenSim", "0.5")]

namespace Dreamnation
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "Translator")]

    public class TranslatorModule : INonSharedRegionModule, ITranslatorModule
    {
        private static readonly ILog m_log =
                LogManager.GetLogger (MethodBase.GetCurrentMethod ().DeclaringType);

        private const int WD_TIMEOUT_MS = 60000;

        private static bool runthread;
        private static Dictionary<string,Translation> translations = new Dictionary<string,Translation> ();
        private static int numregions;
        private static object queuelock = new object ();
        private static Queue<Translation> translationq = new Queue<Translation> ();
        private static Thread mythread;

        public void Initialise (IConfigSource config)
        {
            m_log.Info ("[Translator]: Initialise*:");

            /*****
            // wrap this in a try block so that defaults will work if
            // the config file doesn't specify otherwise.
            int maxlisteners = 1000;
            int maxhandles = 64;
            try
            {
                m_whisperdistance = config.Configs["Chat"].GetInt(
                        "whisper_distance", m_whisperdistance);
                m_saydistance = config.Configs["Chat"].GetInt(
                        "say_distance", m_saydistance);
                m_shoutdistance = config.Configs["Chat"].GetInt(
                        "shout_distance", m_shoutdistance);
                maxlisteners = config.Configs["LL-Functions"].GetInt(
                        "max_listens_per_region", maxlisteners);
                maxhandles = config.Configs["LL-Functions"].GetInt(
                        "max_listens_per_script", maxhandles);
            }
            catch (Exception)
            {
            }
            if (maxlisteners < 1) maxlisteners = int.MaxValue;
            if (maxhandles < 1) maxhandles = int.MaxValue;
            m_listenerManager = new ListenerManager(maxlisteners, maxhandles);
            m_pendingQ = new Queue();
            m_pending = Queue.Synchronized(m_pendingQ);
            *****/
        }

        public void PostInitialise ()
        { }

        public void AddRegion (Scene scene)
        {
            scene.RegisterModuleInterface<ITranslatorModule> (this);
            lock (queuelock) {
                if (++ numregions == 1) {
                    runthread = true;
                    mythread = Watchdog.StartThread (TranslatorThread, "translator", ThreadPriority.Normal, false, true, null, WD_TIMEOUT_MS);
                }
            }
        }

        public void RegionLoaded (Scene scene) { }

        public void RemoveRegion (Scene scene)
        {
            scene.UnregisterModuleInterface<ITranslatorModule> (this);
            lock (queuelock) {
                if (-- numregions == 0) {
                    runthread = false;
                    Monitor.PulseAll (queuelock);
                    mythread = null;
                }
            }
        }

        public void Close ()
        { }

        public string Name
        {
            get { return "Translator"; }
        }

        public Type ReplaceableInterface { get { return null; } }

        /**
         * @brief A client connection was opened, set up context to handle message translations.
         */
        public ITranslatorClient ClientOpened (IClientAPI client)
        {
            return new TranslatorClient (this, client);
        }

        /**
         * @brief Start translating the message, call finished when done.
         */
        private static void Translate (string srclc, string dstlc, string message, ITranslatorFinished finished)
        {
            message = message.Trim ();

            // key for the translations dictionary
            string key = srclc + ":" + dstlc + ":" + message;

            string translated = null;
            lock (queuelock) {
                if (runthread) {

                    // see if we already have this translation either done or in progress
                    Translation val;
                    if (!translations.TryGetValue (key, out val)) {

                        // no, queue the translation for processing
                        val = new Translation ();
                        val.srclc = srclc;
                        val.dstlc = dstlc;
                        val.message = message;
                        translations[key] = val;
                        translationq.Enqueue (val);
                        Monitor.PulseAll (queuelock);
                    }

                    // if translation in progress, queue 'finished' for processing when done
                    translated = val.translated;
                    if (translated == null) {
                        val.onFinished += finished;
                    }
                } else {

                    // translation thread not running (crashed or whatever)
                    translated = "[[[" + srclc + "]]]" + message;
                }
            }

            // if translation completed, call finished right now
            if (translated != null) {
                finished (translated);
            }
        }

        /**
         * @brief One of these in the whole process.
         *        It takes pending translations from translationq,
         *        translates the message, saves the translation,
         *        then calls all the Finished events.
         *        The translation remains in the translations
         *        dictionary in case the translation is needed
         *        again.
         */
        private static void TranslatorThread ()
        {
            Monitor.Enter (queuelock);

            try {
                while (runthread) {
                    Watchdog.UpdateThread ();

                    // wait for something to process and dequeue it
                    if (translationq.Count == 0) {
                        Monitor.Wait (queuelock, WD_TIMEOUT_MS / 2);
                        continue;
                    }
                    Translation val = translationq.Dequeue ();
                    Monitor.Exit (queuelock);

                    // do the translation (might take a few seconds)
                    string xlation;
                    try {
                        xlation = DoTranslate (val.srclc, val.dstlc, val.message);
                        if (xlation == null) throw new NullReferenceException ("result null");
                    } catch (Exception e) {
                        m_log.Warn ("[Translator]: failed " + val.srclc + " -> " + val.dstlc, e);
                        xlation = null;
                    }

                    // original text with language code if translation failed
                    if (xlation == null) {
                        xlation = "[[[" + val.srclc + "]]]" + val.message;
                    }

                    // mark entry in translations dictionary as completed
                    Monitor.Enter (queuelock);
                    val.translated = xlation;

                    // see if anything was waiting for the translation to complete
                    // if so, clear out the list and call them while unlocked
                    ITranslatorFinished finisheds = val.GetFinisheds ();
                    if (finisheds != null) {
                        Monitor.Exit (queuelock);
                        finisheds (xlation);
                        Monitor.Enter (queuelock);
                    }
                }
            } catch (Exception e) {
                m_log.Error ("[Translator]: Error in translator thread", e);
            } finally {
                runthread = false;
                try { Monitor.Exit (queuelock); } catch { }
            }
        }

        /**
         * @brief Finally perform translation.
         */
        private static string DoTranslate (string srclc, string dstlc, string message)
        {
            /* ----------------------------------------------------------------------------------------- */

            // Sends the request to Google
            string query = "s=" + HttpUtility.UrlEncode (srclc) +
                          "&d=" + HttpUtility.UrlEncode (dstlc) +
                          "&m=" + HttpUtility.UrlEncode (message);
            return SynchronousHttpRequester.MakeRequest (
                "POST",
                "http://world.dreamnation.net/GoogleTranslate.php",
                "application/x-www-form-urlencoded",
                query,
                WD_TIMEOUT_MS / 2000,
                null
            );

            /* ----------------------------------------------------------------------------------------- */

            /* - only does single words
            // https://networkprogramming.wordpress.com/2013/08/31/translation-api-without-an-api-key/
            // https://en.glosbe.com/a-api
            string xml = SynchronousHttpRequester.MakeRequest (
                "GET",
                "https://glosbe.com/gapi/translate" +
                        "?from=" + srclc +
                        "&dest=" + dstlc +
                        "&format=json" +
                        "&phrase=" + HttpUtility.UrlEncode (message) +
                        "&pretty=true&tm=false",
                "application/x-www-form-urlencoded",
                null,
                WD_TIMEOUT_MS / 2000,
                null
            );
            XmlDocument doc = new XmlDocument ();
            doc.LoadXml (str);
            XmlNode docMap = doc.GetElementsByTagName ("map")[0];
            */

            /* ----------------------------------------------------------------------------------------- */

            /* works but kinda slow...
            // http://mymemory.translated.net/doc/spec.php
            string obj = "q=" + HttpUtility.UrlEncode (message) +
                        "&langpair=" + HttpUtility.UrlEncode (srclc) + "|" + HttpUtility.UrlEncode (dstlc) +
                        "&de=mikemig@nii.net";
            string json = SynchronousHttpRequester.MakeRequest (
                "POST",
                "http://api.mymemory.translated.net/get",
                "application/x-www-form-urlencoded",
                obj,
                WD_TIMEOUT_MS / 2000,
                null
            );

            int i = json.IndexOf ("\"translatedText\":\"");
            if (i < 0) throw new ArgumentException ("missing translatedText");
            i += 18;
            int j = json.IndexOf ('"', i);
            if (j < 0) j = json.Length;
            json = json.Substring (i, j - i);

            i = 0;
            while ((i = json.IndexOf ("\\u", i)) >= 0) {
                int c;
                try {
                    c = Convert.ToInt32 (json.Substring (i + 2, 4), 16);
                    json = json.Substring (0, i) + ((char) c) + json.Substring (i + 6);
                    i ++;
                } catch {
                    i += 2;
                }
            }
            return json;
            */
        }

        /**
         * @brief One of these per client connected to the sim.
         */
        private class TranslatorClient : ITranslatorClient {
            private const int    PUBLIC_CHANNEL   = 0;  // from scripts
            private const string DEFAULT_LANGCODE = "en";
            private const string DISABLE_LANGCODE = "off";
            private const string NOTRANS_LANGCODE = "--";

            private IClientAPI client;
            private string langcode;
            private TranslatorModule module;

            public TranslatorClient (TranslatorModule mod, IClientAPI cli)
            {
                module = mod;
                client = cli;
            }

            public void ClientClosed ()
            { }

            /**
             * @Brief Message from client to chat or IM.
             */
            public void ClientToWhatev (ITranslatorFinished finished, string message, int channel)
            {
                // don't translate message headed for scripts
                // it's probably something like a button from a menu
                if (channel != PUBLIC_CHANNEL) {
                    finished (message);
                    return;
                }

                int i = message.IndexOf ("[[[");
                int j = message.IndexOf ("]]]");

                // translator commands begin with [[[ and end with ]]]
                if ((i == 0) && (j == message.Length - 3)) {
                    string newlc = message.Substring (3, message.Length - 6).ToLowerInvariant ();
                    if (newlc == DISABLE_LANGCODE) newlc = DEFAULT_LANGCODE;

                    string reply;
                    if (newlc == NOTRANS_LANGCODE) {
                        reply = "pass-through mode";
                        langcode = newlc;
                    } else if (ValidLanguageCode (newlc)) {
                        reply = "language code set to " + newlc;
                        langcode = newlc;
                    } else {
                        reply = "unknown language code " + newlc;
                    }

                    // echo acknowledgement back to client without translation
                    client.SendChatMessage ("[[[" + NOTRANS_LANGCODE + "]]]" + reply,
                            (byte) ChatTypeEnum.Owner, Vector3.Zero, "Translator", UUID.Zero,
                            UUID.Zero, (byte) ChatSourceType.System, (byte) ChatAudibleLevel.Fully);

                    // don't pass the message into the sim for further processing
                    finished (null);
                } else {

                    // otherwise, if no explicit [[[lc]]] prefix, put in the client's current langcode setting
                    if ((i != 0) || (j < 0)) {
                        if (langcode == null) langcode = DEFAULT_LANGCODE;
                        message = "[[[" + langcode + "]]]" + message;
                    }
                    finished (message);
                }
            }

            /**
             * @brief Message from chat or IM to client.
             */
            public void WhatevToClient (ITranslatorFinished finished, string message)
            {
                if (langcode == null) {
                    langcode = DEFAULT_LANGCODE;
                }

                // see if message coming from sim has a language tag on it
                // [[[languagecode]]]
                // if not, put the default code on it

                int i = message.IndexOf ("[[[");
                int j = message.IndexOf ("]]]");
                if ((i > 0) || (j < 0)) {
                    if (langcode == DEFAULT_LANGCODE) {
                        finished (message);
                        return;
                    }
                    message = "[[[" + DEFAULT_LANGCODE + "]]]" + message;
                    j = message.IndexOf ("]]]");
                }

                // separate out the language code of the message from the rest of the message
                string msglc = message.Substring (3, j - 3);
                message = message.Substring (j + 3);

                // if message's language matches the client's language, pass message to client as is
                // also pass message as is if it was tagged with [[[--]]]
                if ((msglc == NOTRANS_LANGCODE) || (msglc == langcode)) {
                    finished (message);
                    return;
                }

                // otherwise, start translating then pass translation to client
                Translate (msglc, langcode, message, finished);
            }

            /**
             * @brief Client requesting the given language code, see if it is valid.
             */
            private bool ValidLanguageCode (string lc)
            {
                return true;
            }
        }

        /**
         * @brief One of these per message being translated.
         */
        private class Translation {
            public string srclc;
            public string dstlc;
            public string message;
            public string translated;

            public event ITranslatorFinished onFinished;
            public ITranslatorFinished GetFinisheds ()
            {
                ITranslatorFinished finisheds = onFinished;
                onFinished = null;
                return finisheds;
            }
        }
    }
}
