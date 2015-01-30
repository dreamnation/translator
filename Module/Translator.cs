
using log4net;
using Nini.Config;
using Mono.Addins;

using OpenMetaverse;

using OpenSim.Framework;
using OpenSim.Framework.Client;
using OpenSim.Framework.Monitoring;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;

using LSL_Integer = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLInteger;
using LSL_String  = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLString;

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;

[assembly: Addin ("TranslatorModule", "0.1")]
[assembly: AddinDependency ("OpenSim", "0.5")]
namespace Dreamnation
{
    public class TranslatorFinished {
        public TranslatorFinished nextfin;
        public bool showorig;
        public ITranslatorFinished finished;
    }

    public interface ITranslatorService {
        string[] AllLangCodes { get; }
        string DefLangCode { get; }
        string Name { get; }
        string Translate (string agentID, string srclc, string dstlc, string message);
    }

    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "Translator")]
    public class TranslatorModule : INonSharedRegionModule, ITranslatorModule
    {
        public static readonly ILog m_log =
                LogManager.GetLogger (MethodBase.GetCurrentMethod ().DeclaringType);

        private const int CACHE_CHARS    = 10000000;
        private const int CACHE_SECS     = 24*60*60;
        public  const int WD_TIMEOUT_MS  = 60000;
        private const int PUBLIC_CHANNEL = 0;  // from scripts
        private const string NOTRANSLATE = "--";

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

        private Dictionary<IClientAPI,TranslatorClient> translatorClients = new Dictionary<IClientAPI,TranslatorClient> ();
        private IGridUserService gridUserService;

        public void Initialise (IConfigSource config)
        {
            m_log.Info ("[Translator]: Initialise");
        }

        public void PostInitialise ()
        { }

        public void AddRegion (Scene scene)
        {
            scene.RegisterModuleInterface<ITranslatorModule> (this);
            gridUserService = scene.RequestModuleInterface<IGridUserService> ();
            lock (queuelock) {
                if (service == null) {
                    service = new TranslatorServiceGoogle (); // TranslatorServiceMyMemory (scene);

                    // make dictionary of all language codes we accept
                    // accept both the two-letter name and the full name
                    // map both those cases to the two-letter code
                    string[] lcs = service.AllLangCodes;
                    allLangCodes = new string[lcs.Length+1];
                    allLangCodes[0] = NOTRANSLATE + " DISABLE";
                    Array.Copy (lcs, 0, allLangCodes, 1, lcs.Length);

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

                if (++ numregions == 1) {
                    runthread = true;
                    Watchdog.StartThread (TranslatorThread, "translator", ThreadPriority.Normal,
                                          false, true, null, WD_TIMEOUT_MS);
                }

                MainConsole.Instance.Commands.AddCommand (
                    "translator",
                    false,
                    "translator",
                    "translator [...|help|...]",
                    "run translator commands",
                    ConsoleCommand
                );
            }
        }

        public void RegionLoaded (Scene scene)
        { }

        public void RemoveRegion (Scene scene)
        {
            scene.UnregisterModuleInterface<ITranslatorModule> (this);
            gridUserService = null;
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
         * @brief Message from chat or IM to agent, whether logged in or not.
         */
        public void WhatevToAgent (string agentID, ITranslatorFinished finished, string message)
        {
            bool   showorig;
            string langcode = GetAgentLangCode (agentID, out showorig);
            WhatevToAgent (agentID, langcode, showorig, finished, message);
        }

        /**
         * @brief Message from chat or IM to agent, whether logged in or not.
         *
         *        Messages should have [[[lc]]] tag on the front as inserted by
         *        ClientToWhatev() and so we know if we need to translate the
         *        message before passing it to the client.
         *
         *        We may get messages without [[[lc]]] (eg, script generated)
         *        in which case we assume they are the default language, then
         *        translate if client is other than the default language.
         *
         *        We may also get messages with [[[--]]] on the front, in which
         *        case we always pass them on without doing any translation.
         *
         *        If scripts generate messages in other than the default language,
         *        they can be prefixed with [[[lc]]] indicating the messages'
         *        actual language.
         *
         * @param agentID  = agent the message is headed to
         * @param langcode = language to translate message to
         * @param showorig = show original text
         * @param finished = what to call when translation is complete
         * @param message  = message to be translated, possibly with [[[lc]]]
         *                   prefix indicating the message's language
         */
        private void WhatevToAgent (string agentID, string langcode, bool showorig, ITranslatorFinished finished, string message)
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

            // if message's language matches the agent's language, pass message to agent as is
            // also, no translation if message is marked notranslate or agent is maked notranslate
            // and no translation for null messages
            if ((msglc == langcode) || (msglc == NOTRANSLATE) || (langcode == NOTRANSLATE) || (message.Trim () == "")) {
                finished (message);
            } else {
                // otherwise, start translating then pass translation to agent
                Translate (msglc, langcode, showorig, message, agentID, finished);
            }
        }

        /**
         * @brief See if GridUser table has a LangCode for this user.
         *        If not, assume default language until script says otherwise.
         */
        private string GetAgentLangCode (string agentID, out bool showorig)
        {
            string langcode = null;
            GridUserInfo gui = gridUserService.GetGridUserInfo (agentID);
            if (gui != null) {
                langcode = gui.LangCode;
            }
            if ((langcode == null) || (langcode == "")) {
                langcode = service.DefLangCode;
            }
            showorig = langcode.EndsWith ("+");
            if (showorig) {
                langcode = langcode.Substring (0, langcode.Length - 1);
            }
            return langcode;
        }

        /**
         * @brief Process console commands.
         */
        private void ConsoleCommand (string module, string[] args)
        {
            if (args.Length < 2) {
                m_log.Info ("[Translator]: missing command, try 'translator help'");
                return;
            }

            switch (args[1]) {
                case "help": {
                    m_log.Info ("[Translator]:  translator status     list people using the translator in this sim");
                    break;
                }
                case "status": {
                    lock (translatorClients) {
                        int count = translatorClients.Count;
                        m_log.Info ("[Translator]: " + count + " " + ((count == 1) ? "person" : "people") + " using translator in this sim");
                        foreach (TranslatorClient tc in translatorClients.Values) {
                            m_log.Info ("[Translator]:   " + tc.langcode + " : " + tc.client.Name);
                        }
                        m_log.Info ("[Translator]: service " + service.Name);
                        m_log.Info ("[Translator]: thread " + (runthread ? "running" : "aborted"));
                    }
                    break;
                }
                default: {
                    m_log.Info ("[Translator]: unknown command, try 'translator help'");
                    break;
                }
            }
        }

        /**
         * @brief Validate language code and return corresponding lower-case 2-letter code.
         *        Return null if unknown code given.
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
        private static void Translate (string srclc, string dstlc, bool showorig, string message, string agentID, ITranslatorFinished finished)
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
                        val.agentID = agentID;
                        val.key = key;
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
                        TranslatorFinished tf = new TranslatorFinished ();
                        tf.nextfin  = val.onFinished;
                        tf.finished = finished;
                        tf.showorig = showorig;
                        val.onFinished = tf;
                    }
                } else {

                    // translation thread not running (crashed or whatever)
                    xlation = "[[[" + srclc + "]]]" + message;
                    showorig = false;
                }
            }

            // if translation completed, call finished right now
            if (xlation != null) {
                if (showorig) xlation += "\n[[[" + srclc + "]]]" + message;
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

                    // do the translation (might take a few seconds)
                    string incorig, xlation;
                    Monitor.Exit (queuelock);
                    try {
                        try {
                            xlation = service.Translate (val.agentID, val.srclc, val.dstlc, val.original);
                            if (xlation == null) throw new ApplicationException ("result null");
                            xlation = xlation.Trim ();
                            if (xlation == "") throw new ApplicationException ("result empty");
                        } catch (Exception e) {
                            m_log.Warn ("[Translator]: failed " + val.srclc + " -> " + val.dstlc + ": ", e);
                            m_log.Info ("[Translator]: original=<" + val.original + ">");
                            xlation = null;
                        }

                        // original text with language code if translation failed
                        incorig = "[[[" + val.srclc + "]]]" + val.original;
                        if (xlation == null) {
                            xlation = incorig;
                        } else {
                            incorig = xlation + "\n" + incorig;
                        }
                    } finally {
                        Monitor.Enter (queuelock);
                    }

                    // mark entry in translations dictionary as completed
                    val.xlation = xlation;
                    val.agentID = null;

                    // see if anything was waiting for the translation to complete
                    // if so, clear out the list and call them whilst unlocked
                    TranslatorFinished tfs = val.onFinished;
                    if (tfs != null) {
                        val.onFinished = null;
                        Monitor.Exit (queuelock);
                        try {
                            do {
                                tfs.finished (tfs.showorig ? incorig : xlation);
                                tfs = tfs.nextfin;
                            } while (tfs != null);
                        } finally {
                            Monitor.Enter (queuelock);
                        }
                    }
                }
            } catch (Exception e) {
                m_log.Error ("[Translator]: Error in translator thread", e);
            } finally {
                runthread = false;
                Monitor.Exit (queuelock);
            }
        }

        /**
         * @brief One of these per client connected to the sim.
         */
        private class TranslatorClient : ITranslatorClient {
            private bool             showorig;
            public  IClientAPI       client;
            public  string           langcode;
            private TranslatorModule module;

            public TranslatorClient (TranslatorModule mod, IClientAPI cli)
            {
                client = cli;
                module = mod;

                langcode = module.GetAgentLangCode (client.AgentId.ToString (), out showorig);

                /*
                 * Module wants to know what clients are instantiated.
                 */
                lock (module.translatorClients) {
                    module.translatorClients[client] = this;
                }
            }

            public void ClientClosed ()
            {
                lock (module.translatorClients) {
                    module.translatorClients.Remove (client);
                }
            }

            /**
             * @brief A script owned by this client is calling osTranslatorControl().
             */
            public object[] ScriptControl (object script, string cmd, object[] args)
            {
                int nargs = args.Length;
                switch (cmd) {

                    /*
                     * Get all supported language codes.
                     */
                    case "getalllangcodes": {
                        int ncod = allLangCodes.Length;
                        object[] rets = new object[ncod];
                        for (int i = 0; i < ncod; i ++) {
                            rets[i] = new LSL_String (allLangCodes[i]);
                        }
                        return rets;
                    }

                    /*
                     * Get default language code.
                     */
                    case "getdeflangcode": {
                        return new object[] { new LSL_String (service.DefLangCode) };
                    }

                    /*
                     * Get current language code.
                     */
                    case "getlangcode": {
                        return new object[] { new LSL_String (langcode) };
                    }

                    /*
                     * Get translation service name.
                     */
                    case "getservicename": {
                        return new object[] { new LSL_String (service.Name) };
                    }

                    /*
                     * Get current show original setting.
                     */
                    case "getshoworig": {
                        return new object[] { new LSL_Integer (showorig ? 1 : 0) };
                    }

                    /*
                     * Set current language code.
                     */
                    case "setlangcode": {

                        /*
                         * Get and validate given language code and convert to 2-letter lower case code.
                         */
                        if (nargs < 1) {
                            return new object[] { new LSL_String ("missing lang code") };
                        }
                        string lc = CheckLangCode (args[0].ToString ());
                        if (lc == null) {
                            return new object[] { new LSL_String ("bad lang code") };
                        }

                        /*
                         * Try to write it to database.
                         * Include trailing '+' iff 'show originals' mode enabled.
                         */
                        string lcplus = lc;
                        if (showorig) lcplus += "+";
                        if (!module.gridUserService.SetLangCode (client.AgentId.ToString (), lcplus)) {
                            m_log.Error ("[Translator]: GridUser.SetLangCode (" + client.AgentId.ToString () + ", " + lcplus + ") failed");
                            return new object[] { new LSL_String ("database write failed") };
                        }

                        /*
                         * Success, save cached value and return success status.
                         */
                        langcode = lc;
                        return new object[] { new LSL_String ("OK") };
                    }

                    /*
                     * Set current show original setting.
                     */
                    case "setshoworig": {

                        /*
                         * Get and validate given setting, should be 0 or 1.
                         */
                        if (nargs < 1) {
                            return new object[] { new LSL_String ("missing setting") };
                        }
                        int setting;
                        if (!int.TryParse (args[0].ToString (), out setting) || (setting < 0) || (setting > 1)) {
                            return new object[] { new LSL_String ("bad setting") };
                        }

                        /*
                         * Try to write it to database.
                         */
                        string lcplus = langcode;
                        if (setting != 0) lcplus += "+";
                        if (!module.gridUserService.SetLangCode (client.AgentId.ToString (), lcplus)) {
                            m_log.Error ("[Translator]: GridUser.SetLangCode (" + client.AgentId.ToString () + ", " + lcplus + ") failed");
                            return new object[] { new LSL_String ("database write failed") };
                        }

                        /*
                         * Success, save cached value and return success status.
                         */
                        showorig = setting != 0;
                        return new object[] { new LSL_String ("OK") };
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
                        string given = message.Substring (3, j - 3).Trim ();
                        if (given != NOTRANSLATE) {
                            string lclo = CheckLangCode (given);
                            if (lclo == null) {
                                client.SendChatMessage ("unknown language code " + given,
                                        (byte) ChatTypeEnum.Owner, Vector3.Zero, "Translator", UUID.Zero,
                                        UUID.Zero, (byte) ChatSourceType.System, (byte) ChatAudibleLevel.Fully);
                                message = null;  // don't pass bad message to sim
                            } else {
                                message = "[[[" + lclo + message.Substring (j);
                            }
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
             */
            public void WhatevToClient (ITranslatorFinished finished, string message)
            {
                module.WhatevToAgent (client.AgentId.ToString (), langcode, showorig, finished, message);
            }
        }

        /**
         * @brief One of these per message being translated.
         */
        private class Translation {
            public DateTime lastuse;                     // last time this translation used
            public TranslatorFinished onFinished;        // waiting for translation complete
            public string agentID;                       // null after translation complete
            public LinkedListNode<Translation> uselink;  // link for oldtranslations linked list
            public string key;                           // key used in translation dictionary
            public string xlation;                       // null before translation complete

            public string srclc
            {
                get {
                    int i = key.IndexOf (':');
                    return key.Substring (0, i);
                }
            }

            public string dstlc
            {
                get {
                    int i = key.IndexOf (':');
                    int j = key.IndexOf (':', ++ i);
                    return key.Substring (i, j - i);
                }
            }

            public string original
            {
                get {
                    int i = key.IndexOf (':');
                    int j = key.IndexOf (':', ++ i);
                    return key.Substring (++ j);
                }
            }

            public Translation ()
            {
                uselink = new LinkedListNode<Translation> (this);
            }
        }
    }
}
