/**
 * Works with the Translator.cs module to control the translator.
 * Provides a menu so the wearer can choose his/her language.
 * Resends the translation command when the wearer moves to new region.
 *
 * Copyright 2015, Kunta Kinte of www.dreamnation.net
 *
 *  This program is free software: you can redistribute it and/or modify
 *  it under the terms of the GNU General Public License as published by
 *  the Free Software Foundation, either version 3 of the License, or
 *  (at your option) any later version.
 *
 *  This program is distributed in the hope that it will be useful,
 *  but WITHOUT ANY WARRANTY; without even the implied warranty of
 *  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 *  GNU General Public License for more details.
 *
 *  You should have received a copy of the GNU General Public License
 *  along with this program.  If not, see <http: *www.gnu.org/licenses/>.
 *
 * v1.1 make sure listen is set up on region change
 * v1.2 show/hide original text option
 */
 
integer channel = -1309628754;
string  version = "v1.2";

integer codepage;
integer numalllangcodes;
integer currentshoworig;
list    alllangcodes;
string  currentlangcode;

/**
 * @brief Get what server thinks our language code is.
 */
SyncUpWithServer ()
{
    currentlangcode = llList2String  (osTranslatorControl ("getlangcode", [ ]), 0);
    currentshoworig = llList2Integer (osTranslatorControl ("getshoworig", [ ]), 0);
    string sho = "hide";
    if (currentshoworig) sho = "show";
    llOwnerSay ("using language " + currentlangcode + " (" + sho + " original)");
}

/**
 * @brief Tell this sim what language we speak.
 */
SetCurrentLangCode ()
{
    // pass it whatever is in currentlangcode
    // it will accept either a 2-letter code
    // or the full name string
    list rets = osTranslatorControl ("setlangcode", [ currentlangcode ]);
    if (llList2String (rets, 0) != "OK") {
        llOwnerSay ("error setting language code to " + currentlangcode);
    }

    // pass it whatever is in currentshoworig
    // it will accept either 0 or 1
    rets = osTranslatorControl ("setshoworig", [ currentshoworig ]);
    if (llList2String (rets, 0) != "OK") {
        llOwnerSay ("error setting show original to " + currentshoworig);
    }

    // good or bad, make sure we know what the sim thinks at this point
    SyncUpWithServer ();
}

/**
 * @brief Get the name part of a "twoletter name" string.
 */
string LangCodeButton (integer index)
{
    if (index >= numalllangcodes) return "-";
    string twoletspname = llList2String (alllangcodes, index);
    integer i = llSubStringIndex (twoletspname, " ");
    return llGetSubString (twoletspname, i + 1, -1);
}

/**
 * @brief Show a page of the language selection dialog.
 */
ShowCodeDialogPage ()
{
    if (codepage < 0) codepage = 0;

    // page 0 is special
    if (codepage == 0) {
        list buttons = [ "-", "-", ">>", "-", "-", "-", "-", "HELP", "-", "HideOriginal", "-", "ShowOriginal" ];
        llDialog (llGetOwner (), "Select Language", buttons, channel);
        return;
    }

    // put up to 10 language names per page
    integer index = (codepage - 1) * 10;

    //  [b9]  [ba]  [bb]
    //  [b6]  [b7]  [b8]
    //  [b3]  [b4]  [b5]
    //  [b0]  [b1]  [b2]

    string b9 = LangCodeButton (index ++);
    string ba = LangCodeButton (index ++);
    string bb = LangCodeButton (index ++);
    string b6 = LangCodeButton (index ++);
    string b7 = LangCodeButton (index ++);
    string b8 = LangCodeButton (index ++);
    string b3 = LangCodeButton (index ++);
    string b4 = LangCodeButton (index ++);
    string b5 = LangCodeButton (index ++);
    string b0 = "<<";
    string b1 = LangCodeButton (index ++);
    string b2 = "-";

    if (index < numalllangcodes) b2 = ">>";

    list buttons = [ b0, b1, b2, b3, b4, b5, b6, b7, b8, b9, ba, bb ];

    // display the buttons
    llDialog (llGetOwner (), "Select Language", buttons, channel);
}

default
{
    /**
     * @brief Script initialization.
     */
    state_entry ()
    {
        // sync up with whatever language the server thinks we are using
        SyncUpWithServer ();

        // tell user there is a menu available
        llOwnerSay ("[" + version + "] touch to get menu");
        state running;
    }
}

state running {

    state_entry ()
    {
        llListen (channel, "", llGetOwner (), "");
    }

    /**
     * @brief If we change regions, send the new sim our language code and re-enable listening.
     */
    changed (integer change)
    {
        if (change & CHANGED_OWNER) {
            llResetScript ();
        }
        if (change & (CHANGED_REGION | CHANGED_TELEPORT | CHANGED_REGION_START)) {
            SetCurrentLangCode ();
            state resetlistens;
        }
    }

    /**
     * @brief User just logged in or object was just attached from inventory.
     */
    on_rez (integer start)
    {
        SetCurrentLangCode ();
        state resetlistens;
    }

    /**
     * @brief Give the user the language selection dialog if attachment is touched.
     */
    touch_start ()
    {
        if (llDetectedKey (0) == llGetOwner ()) {
            alllangcodes = osTranslatorControl ("getalllangcodes", [ ]);
            numalllangcodes = llGetListLength (alllangcodes);
            ShowCodeDialogPage ();
        }
    }

    /**
     * @brief Process language menu selection.
     */
    listen (integer chan, string name, key id, string message)
    {
        if (message == "HELP") {
            llOwnerSay ("Go to http://wiki.dreamnation.net and click on Chat/IM Language Translator");
        } else if (message == "<<") {
            -- codepage;
            ShowCodeDialogPage ();
        } else if (message == ">>") {
            codepage ++;
            ShowCodeDialogPage ();
        } else if (message == "-") {
            ShowCodeDialogPage ();
        } else if (message == "HideOriginal") {
            currentshoworig = FALSE;
            SetCurrentLangCode ();
        } else if (message == "ShowOriginal") {
            currentshoworig = TRUE;
            SetCurrentLangCode ();
        } else {
            currentlangcode = message;
            SetCurrentLangCode ();
        }
    }
}

/**
 * @brief Temporary state to make sure we have one and only one listen.
 */
state resetlistens {
    state_entry ()
    {
        state running;
    }
}
