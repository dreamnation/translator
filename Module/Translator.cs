
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

    public class Translator : INonSharedRegionModule, ITranslatorModule
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
        {
        }

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
                    mythread.Join ();
                    mythread = null;
                }
            }
        }

        public void Close ()
        {
        }

        public string Name
        {
            get { return "Translator"; }
        }

        public Type ReplaceableInterface { get { return null; } }

        ///////////////////////////////////////////////////////

        public string DefaultLanguageCode { get { return "en"; } }

        public bool ValidLanguageCode (IClientAPI client, string lc)
        {
            return true;
        }

        public void Translate (IClientAPI client, string srclc, string dstlc, string message, ITranslatorModuleFinished finished)
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

            // if translation completed, call 'finished' right now
            if (translated != null) {
                finished (translated);
            }
        }

        ///////////////////////////////////////////////////////

        /**
         * @brief One of these per message being translated.
         */
        private class Translation {
            public string srclc;
            public string dstlc;
            public string message;
            public string translated;

            public event ITranslatorModuleFinished onFinished;
            public ITranslatorModuleFinished GetFinisheds ()
            {
                ITranslatorModuleFinished finisheds = onFinished;
                onFinished = null;
                return finisheds;
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
                    ITranslatorModuleFinished finisheds = val.GetFinisheds ();
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
    }
}
