
using log4net;
using Nini.Config;
using Mono.Addins;

using OpenMetaverse;

using OpenSim.Framework;
using OpenSim.Framework.Client;
using OpenSim.Framework.Monitoring;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

using LSL_String = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLString;

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;

[assembly: Addin ("TranslatorModule", "0.1")]
[assembly: AddinDependency ("OpenSim", "0.5")]
namespace Dreamnation
{
    public interface ITranslatorService {
        string[] AllLangCodes { get; }
        string DefLangCode { get; }
        string Name { get; }
        string Translate (IClientAPI client, string srclc, string dstlc, string message);
    }

    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "Translator")]
    public class TranslatorModule : INonSharedRegionModule, ITranslatorModule
    {
        private static readonly ILog m_log =
                LogManager.GetLogger (MethodBase.GetCurrentMethod ().DeclaringType);

        private const int CACHE_CHARS    = 10000000;
        private const int CACHE_SECS     = 24*60*60;
        public  const int WD_TIMEOUT_MS  = 60000;
        private const int PUBLIC_CHANNEL = 0;  // from scripts

        private static bool runthread;
        private static Dictionary<string,string> langcodedict;
        private static Dictionary<string,Translation> translations = new Dictionary<string,Translation> ();
        private static int numcachedchars;
        private static int numregions;
        private static ITranslatorService service;
        private static LinkedList<Translation> oldtranslations = new LinkedList<Translation> ();
        private static object queuelock = new object ();
        private static Queue<Translation> translationq = new Queue<Translation> ();
        private static string[] allLangCodes;

        public void Initialise (IConfigSource config)
        {
            m_log.Info ("[Translator]: Initialise");
            service = new TranslatorServiceGoogle (); // TranslatorServiceMyMemory ();

            // make dictionary of all language codes we accept
            // accept both the two-letter name and the full name
            // map both those cases to the two-letter code
            allLangCodes = service.AllLangCodes;
            Dictionary<string,string> lcd = new Dictionary<string,string> ();
            foreach (string alc in allLangCodes) {
                string alclo = alc.ToLowerInvariant ();
                int i = alclo.IndexOf (' ');
                string twolet = alclo.Substring (0, i);
                lcd.Add (twolet, twolet);
                lcd.Add (alclo.Substring (++ i), twolet);
            }
            langcodedict = lcd;
        }

        public void PostInitialise ()
        { }

        public void AddRegion (Scene scene)
        {
            scene.RegisterModuleInterface<ITranslatorModule> (this);
            lock (queuelock) {
                if (++ numregions == 1) {
                    runthread = true;
                    Watchdog.StartThread (TranslatorThread, "translator", ThreadPriority.Normal,
                                          false, true, null, WD_TIMEOUT_MS);
                }
            }
        }

        public void RegionLoaded (Scene scene)
        { }

        public void RemoveRegion (Scene scene)
        {
            scene.UnregisterModuleInterface<ITranslatorModule> (this);
            lock (queuelock) {
                if (-- numregions == 0) {
                    runthread = false;
                    Monitor.PulseAll (queuelock);
                }
            }
        }

        public void Close ()
        { }

        public string Name
        {
            get { return "Translator"; }
        }

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        /**
         * @brief A client connection was opened, set up context to handle message translations.
         */
        public ITranslatorClient ClientOpened (IClientAPI client)
        {
            return new TranslatorClient (this, client);
        }

        /**
         * @brief Validate language code and return corresponding lower-case 2-letter code.
         */
        private static string CheckLangCode (string lc)
        {
            string lclo = lc.Trim ().ToLowerInvariant ();
            string twolet;
            langcodedict.TryGetValue (lclo, out twolet);
            return twolet;
        }

        /**
         * @brief Start translating the message, call finished when done.
         */
        private static void Translate (string srclc, string dstlc, string message, IClientAPI client, ITranslatorFinished finished)
        {
            message = message.Trim ();

            // key for the translations dictionary
            string key = srclc + ":" + dstlc + ":" + message;

            string xlation = null;
            lock (queuelock) {
                if (runthread) {

                    // see if we already have this translation either done or in progress
                    Translation val;
                    if (translations.TryGetValue (key, out val)) {
                        oldtranslations.Remove (val.uselink);
                    } else {

                        // no, queue the translation for processing
                        val = new Translation ();
                        val.client = client;
                        val.key = key;
                        val.srclc = srclc;
                        val.dstlc = dstlc;
                        val.original = message;
                        translations[key] = val;
                        translationq.Enqueue (val);
                        numcachedchars += key.Length;
                        Monitor.PulseAll (queuelock);
                    }

                    // make it the newest translation around
                    val.lastuse = DateTime.UtcNow;
                    oldtranslations.AddLast (val.uselink);

                    // if translation in progress, queue 'finished' for processing when done
                    xlation = val.xlation;
                    if (xlation == null) {
                        val.onFinished += finished;
                    }
                } else {

                    // translation thread not running (crashed or whatever)
                    xlation = "[[[" + srclc + "]]]" + message;
                }
            }

            // if translation completed, call finished right now
            if (xlation != null) {
                finished (xlation);
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

                    // take old translations out of cache
                    DateTime utcnow = DateTime.UtcNow;
                    LinkedListNode<Translation> lln;
                    Translation val;
                    while ((lln = oldtranslations.First) != null) {
                        val = lln.Value;
                        if (val.xlation == null) break;
                        if ((numcachedchars < CACHE_CHARS) &&
                            (utcnow.Subtract (val.lastuse).TotalSeconds < CACHE_SECS)) break;
                        numcachedchars -= val.key.Length;
                        oldtranslations.Remove (lln);
                        translations.Remove (val.key);
                    }

                    // wait for something to process and dequeue it
                    if (translationq.Count == 0) {
                        Monitor.Wait (queuelock, WD_TIMEOUT_MS / 2);
                        continue;
                    }
                    val = translationq.Dequeue ();
                    Monitor.Exit (queuelock);

                    // do the translation (might take a few seconds)
                    string xlation;
                    try {
                        xlation = service.Translate (val.client, val.srclc, val.dstlc, val.original);
                        if (xlation == null) throw new NullReferenceException ("result null");
                        xlation = xlation.Trim ();
                        if (xlation == "") throw new NullReferenceException ("result empty");
                    } catch (Exception e) {
                        m_log.Warn ("[Translator]: failed " + val.srclc + " -> " + val.dstlc, e);
                        xlation = null;
                    }

                    // original text with language code if translation failed
                    if (xlation == null) {
                        xlation = "[[[" + val.srclc + "]]]" + val.original;
                    }

                    // mark entry in translations dictionary as completed
                    Monitor.Enter (queuelock);
                    val.xlation = xlation;

                    // save some memory
                    val.client   = null;
                    val.srclc    = null;
                    val.dstlc    = null;
                    val.original = null;

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
         * @brief One of these per client connected to the sim.
         */
        private class TranslatorClient : ITranslatorClient {
            private IClientAPI client;
            private string langcode;

            public TranslatorClient (TranslatorModule mod, IClientAPI cli)
            {
                client = cli;
                langcode = service.DefLangCode;
            }

            public void ClientClosed ()
            { }

            /**
             * @brief A script owned by this client is calling osTranslatorControl().
             */
            public object[] ScriptControl (string cmd, object[] args)
            {
                int nargs = args.Length;
                switch (cmd) {
                    case "getdeflangcode": {
                        return new object[] { new LSL_String (service.DefLangCode) };
                    }
                    case "getalllangcodes": {
                        int ncod = allLangCodes.Length;
                        object[] rets = new object[ncod];
                        for (int i = 0; i < ncod; i ++) {
                            rets[i] = new LSL_String (allLangCodes[i]);
                        }
                        return rets;
                    }
                    case "getlangcode": {
                        return new object[] { new LSL_String (langcode) };
                    }
                    case "getservicename": {
                        return new object[] { new LSL_String (service.Name) };
                    }
                    case "setlangcode": {
                        if (nargs < 1) break;
                        string lc = CheckLangCode (args[0].ToString ());
                        if (lc != null) langcode = lc;
                        return new object[] { new LSL_String ((lc != null) ? "OK" : "badlangcode") };
                    }
                }
                return new object[0];
            }

            /**
             * @Brief Message from client to chat or IM.
             *
             *        All we do is tag the message with the client's language code
             *        by putting [[[lc]]] on the front.  But if the message already
             *        has [[[lc]]] on the front, validate then leave it as is.
             *
             *        WhatevToClient() will strip the tag off and use it to determine
             *        if the message needs to be translated or not when passing it
             *        back out to the other client.
             */
            public void ClientToWhatev (ITranslatorFinished finished, string message, int channel)
            {
                // don't tag messages headed for scripts
                // they're probably something like a button from a menu
                if (channel == PUBLIC_CHANNEL) {

                    // if user typed message with a [[[lc]]] prefix,
                    // validate and convert to lower-case 2-letter code
                    int i = message.IndexOf ("[[[");
                    int j = message.IndexOf ("]]]");
                    if ((i == 0) && (j > 0)) {
                        string given = message.Substring (3, j - 3);
                        string lclo = CheckLangCode (given);
                        if (lclo == null) {
                            client.SendChatMessage ("unknown language code " + given,
                                    (byte) ChatTypeEnum.Owner, Vector3.Zero, "Translator", UUID.Zero,
                                    UUID.Zero, (byte) ChatSourceType.System, (byte) ChatAudibleLevel.Fully);
                            message = null;  // don't pass bad message to sim
                        } else {
                            message = "[[[" + lclo + message.Substring (j);
                        }
                    } else {

                        // user didn't give an explicit [[[lc]]] prefix,
                        // use the client's langcode setting to tag message
                        message = "[[[" + langcode + "]]]" + message;
                    }
                }

                finished (message);
            }

            /**
             * @brief Message from chat or IM to client.
             *
             *        Messages should have [[[lc]]] tag on the front as inserted by
             *        ClientToWhatev() and so we know if we need to translate the
             *        message before passing it to the client.
             *
             *        We may get messages without [[[lc]]] (eg, script generated)
             *        in which case we assume they are the default language, then
             *        translate if client is other than the default language.
             *
             *        If scripts generate messages in other than the default language,
             *        they can be prefixed with [[[lc]]] indicating the messages'
             *        actual language.
             */
            public void WhatevToClient (ITranslatorFinished finished, string message)
            {
                // split [[[langcode]]] off front of message
                // if not there, assume it is default language
                string msglc = service.DefLangCode;
                int i = message.IndexOf ("[[[");
                int j = message.IndexOf ("]]]");
                if ((i == 0) && (j > 0)) {
                    msglc = message.Substring (3, j - 3);
                    message = message.Substring (j + 3);
                }

                // if message's language matches the client's language, pass message to client as is
                if (msglc == langcode) {
                    finished (message);
                } else {
                    // otherwise, start translating then pass translation to client
                    Translate (msglc, langcode, message, client, finished);
                }
            }
        }

        /**
         * @brief One of these per message being translated.
         */
        private class Translation {
            public DateTime lastuse;                      // last time this translation used
            public event ITranslatorFinished onFinished;  // waiting for translation complete
            public IClientAPI client;                     // null after translation complete
            public LinkedListNode<Translation> uselink;   // link for oldtranslations linked list
            public string key;                            // key used in translation dictionary
            public string srclc;                          // null after translation complete
            public string dstlc;                          // null after translation complete
            public string original;                       // null after translation complete
            public string xlation;                        // null before translation complete

            public Translation ()
            {
                uselink = new LinkedListNode<Translation> (this);
            }

            public ITranslatorFinished GetFinisheds ()
            {
                ITranslatorFinished finisheds = onFinished;
                onFinished = null;
                return finisheds;
            }
        }
    }
}
