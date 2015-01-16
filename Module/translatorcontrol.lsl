/**
 * Works with the Translator.cs module to control the translator.
 * Provides a menu so the wearer can choose his/her language.
 * Resends the translation command when the wearer moves to new region.
 */
 
integer channel = -1309628754;

integer codepage;
list alllangcodes;
string currentlangcode;

/**
 * @brief Tell this sim what language we speak.
 */
SetCurrentLangCode ()
{
    // pass it whatever is in currentlangcode
    // it will accept either a 2-letter code
    // or the full name string
    list rets = osTranslatorControl ("setlangcode", [ currentlangcode ]);
    if ((llGetListLength (rets) != 1) || (llList2String (rets, 0) != "OK")) {
        llOwnerSay ("error setting language code to " + currentlangcode);
    } else {
        string service = llList2String (osTranslatorControl ("getservicename", [ ]), 0);
        llOwnerSay ("language code set to " + currentlangcode + " [" + service + "]");
    }

    // good or bad, sync up with whatever server thinks
    // this always gets the 2-letter code
    currentlangcode = llList2String (osTranslatorControl ("getlangcode", [ ]), 0);
}

/**
 * @brief Get the name part of a "twoletter name" string.
 */
string LangName (string twoletspname)
{
    integer i = llSubStringIndex (twoletspname, " ");
    return llGetSubString (twoletspname, i + 1, -1);
}

/**
 * @brief Show a page of the language selection dialog.
 */
ShowCodeDialogPage ()
{
    if (codepage < 0) codepage = 0;

    // put up to 10 language names per page
    // alllangcodes has entries of '2-letter fullname' pairs
    integer codeix = codepage * 10;
    integer ncodes = llGetListLength (alllangcodes);
    
    string b9 = (codeix < ncodes) ? LangName (alllangcodes[codeix++]) : "-";
    string ba = (codeix < ncodes) ? LangName (alllangcodes[codeix++]) : "-";
    string bb = (codeix < ncodes) ? LangName (alllangcodes[codeix++]) : "-";
    string b6 = (codeix < ncodes) ? LangName (alllangcodes[codeix++]) : "-";
    string b7 = (codeix < ncodes) ? LangName (alllangcodes[codeix++]) : "-";
    string b8 = (codeix < ncodes) ? LangName (alllangcodes[codeix++]) : "-";
    string b3 = (codeix < ncodes) ? LangName (alllangcodes[codeix++]) : "-";
    string b4 = (codeix < ncodes) ? LangName (alllangcodes[codeix++]) : "-";
    string b5 = (codeix < ncodes) ? LangName (alllangcodes[codeix++]) : "-";
    string b0 = (codepage > 0)    ? "<<" : "-";
    string b1 = (codeix < ncodes) ? LangName (alllangcodes[codeix++]) : "-";
    string b2 = (codeix < ncodes) ? ">>" : "-";

    list buttons = [ b0, b1, b2, b3, b4, b5, b6, b7, b8, b9, ba, bb ];

    // display the buttons
    llDialog (llGetOwner (), "Select Language", buttons, channel);
}

default
{
    /**
     * @brief Script initialization.
     */
    state_entry()
    {
        // sync up with whatever language the server thinks we are using
        currentlangcode = llList2String (osTranslatorControl ("getlangcode", [ ]), 0);

        // tell user there is a menu available
        llOwnerSay ("touch to get menu");
        llListen (channel, "", llGetOwner (), "");
    }

    /**
     * @brief If we change regions, send the new sim our language code.
     */
    changed (integer change)
    {
        if (change & CHANGED_OWNER) {
            llResetScript ();
        }
        if (change & (CHANGED_REGION | CHANGED_TELEPORT | CHANGED_REGION_START)) {
            SetCurrentLangCode ();
        }
    }

    /**
     * @brief Give the user the language selection dialog if we are touched.
     */
    touch_start ()
    {
        if (llDetectedKey (0) == llGetOwner ()) {
            alllangcodes = osTranslatorControl ("getalllangcodes", [ ]);
            codepage = 0;
            ShowCodeDialogPage ();
        }
    }

    /**
     * @brief Process language menu selection.
     */
    listen (integer chan, string name, key id, string message)
    {
        if (message == "<<") {
            -- codepage;
            ShowCodeDialogPage ();
        } else if (message == ">>") {
            codepage ++;
            ShowCodeDialogPage ();
        } else if (message != "-") {
            currentlangcode = message;
            SetCurrentLangCode ();
        }
    }
}
