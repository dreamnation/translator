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
 * @brief Show a page of the language selection dialog.
 */
ShowCodeDialogPage ()
{
    if (codepage < 0) codepage = 0;

    // maybe start out with a 'back' button
    list buttons;
    if (codepage > 0) buttons += "<<";

    // put up to 10 language names per page
    // alllangcodes has entries of '2-letter fullname' pairs
    integer codeix = codepage * 10;
    integer ncodes = llGetListLength (alllangcodes);
    integer i;
    for (i = 0; (i < 10) && (codeix < ncodes); i ++) {

        // get an entry and strip off the 2-letter code from the front
        string lc = llList2String (alllangcodes, codeix ++);
        integer j = llSubStringIndex (lc, " ");
        if (j >= 0) {
            lc = llGetSubString (lc, ++ j, -1);
            buttons += lc;
        }
    }

    // maybe end up with a 'next' button
    if (codeix < ncodes) buttons += ">>";

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
        } else {
            currentlangcode = message;
            SetCurrentLangCode ();
        }
    }
}
