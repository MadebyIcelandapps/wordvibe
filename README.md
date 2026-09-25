# SoundSpell

A small Windows helper for people who spell words the way they sound, made with dyslexia in mind. US and UK spellings both count as right, and it knows Irish names and places.

## How to use it

In any app (Word, a browser, WhatsApp, Discord, Notepad), type the word the way it sounds, then **tap Shift twice**:

| You type                | You get          |
|-------------------------|------------------|
| `nesesary` ⇧⇧           | `necessary`      |
| `see you wensday.` ⇧⇧   | `see you Wednesday.` |
| `neev` ⇧⇧               | `Niamh`          |
| `my favrit` ⇧⇧          | `my favourite`   |

Or type `@@` in front of the word, then a space. The matches show while you are still typing.

A list of matches pops up next to the text:

- **Ctrl+1 … Ctrl+7** or a **click** puts in that word
- **Ctrl+0** puts back what you typed
- **Point at a word** (or press **Ctrl+Shift+number**) to hear it read out
- After `@@word`, **Enter** only fixes the word, so a chat message is not sent yet. Press Enter again to send.

The list closes as soon as you keep typing, click somewhere else, or switch window.

**The sentence strip.** While you type, a frosted-glass strip sits just above your words and shows the sentence so far:

- a soft **green** behind words that are spelled right
- a soft **red** behind words to check

Click a red word to see its matches, or tap Shift twice to fix the nearest red one. The 🔊 button reads the sentence out. Names it doesn't know (a capital in the middle of a sentence) stay plain, and any unknown word can be added to *My words* from its list.

**Reading out loud.** Select any text (an email, a web page, a school handout) and **tap Ctrl twice** to hear it. With nothing selected, it reads the sentence you are typing. Tap Ctrl twice again to stop. In Settings you can pick a **natural voice**. It downloads once (about 60 MB) and runs on the computer itself, so nothing you read is sent anywhere. The British voices are Cori, Jenny, Alba, Alan and a northern English man, and there are two American ones. There isn't an Irish-accent one yet. Until a natural voice is chosen, or if it ever fails, the built-in Windows voice is used.

**Your usual mistakes get fixed by themselves.** Once you have fixed the same misspelling the same way 3 times, it gets fixed as you type from then on, with no shortcut needed. A little note says so, and Ctrl+0 right after puts your spelling back and stops it. Settings has a button to see or change the list.

**Words that sound alike** (there / their / they're, your / you're, weather / whether, and about 70 more) all show up together, each with a short meaning. The word before helps put the right one first: `lost @@there` gives *their*.

**It learns.** A word you pick from the list comes first the next time you spell it that way. If you pick Ctrl+0 twice for your own spelling, your spelling comes first.

## Settings

Right-click the SoundSpell icon by the clock (under the **^** arrow) and choose **Settings**:

- **Shortcut:** Tap Shift twice, Ctrl+Space, Tap right Ctrl, or None (then use `@@` only)
- **Put in the first match by itself**, or only show the list
- **Show the sentence above what I type** (the strip) and **fix my usual mistakes by themselves**
- **Voice:** the Windows voice, or a natural voice (downloads once), with a sample button, plus voice speed
- **Read the fixed word out loud**, **read a match when I point at it**, and **tap Ctrl twice to hear the selected text**
- **Text size**, **colours** (cream, pale yellow, light blue, white, dark) and **font** (Verdana, OpenDyslexic if installed, Comic Sans MS, Tahoma, Arial), with a preview
- **Start when I sign in**
- **Also work in programs running as administrator.** Windows blocks normal programs from typing into admin programs. Turning this on asks Windows once, then starts SoundSpell as administrator at sign-in.
- **Check for updates once a day**, plus a button to check now

The tray menu also has **Try it** (type spellings and see or hear every match), **My words** and **Check for updates**.

**My words** opens a list for names and your own words, one per line. They are found first. Add how a word sounds after `=` to help it be found:

```
Ciara
Mícheál=meehawl
Clonakilty=klonakilty
```

## Install (a few seconds, no admin rights)

1. Download this repository as a zip (**Code → Download ZIP**), or the **SoundSpell** zip from the latest *build* GitHub Actions run, which has the program already built.
2. Unzip it and double-click **`Install.cmd`**.

It is running now and starts with Windows from now on. It builds itself with the C# compiler that is already part of Windows 10 and 11, so nothing else is downloaded.

If Windows shows *"Windows protected your PC"*, choose **More info → Run anyway**. The installer is not signed, because signing needs a paid code-signing certificate.

**Updating:** use **Check for updates** in the tray menu. It downloads the newest version from this repository and installs it over the old one. Settings, *My words* and what it has learned are kept.

**Uninstalling:** Settings → Apps → SoundSpell → Uninstall. *My words* and what it has learned stay in `%APPDATA%\SoundSpell`.

## How it finds the word

Every dictionary word (55,000 common words in both US and UK spelling, plus Irish names and places) gets a "sound key":

- `ph` and `f` sound the same, and so do `c`, `k` and `q`
- `tion` sounds like `shun`, and `-re` sounds like `-er` (centre)
- silent letters are dropped (`kn`, `wr`, `could`)
- all vowel sounds count as one

A typed word is matched on sound first, then spelling, then how common the word is, then what you picked before. Some mix-ups cost less, because they are common with dyslexia or in Irish speech:

- flipped letters (`b`/`d`, `p`/`q`) and swapped neighbours (`freind`)
- dropped vowels
- `th` said as `t` (`tink`)

Some words don't sound like their spelling (`Wednesday`, `receipt`, `Siobhán`, `Taoiseach`), so they are also listed the way they are said.

## Files

- `src/Speller.cs`: the matcher (plain C#, no Windows parts)
- `src/Homophones.cs`: words that sound alike, with meanings
- `src/KeyWatcher.cs`: watches typing, the shortcut, and swaps the word
- `src/Popup.cs`: the list of matches
- `src/SentenceStrip.cs`: the frosted-glass sentence strip
- `src/Caret.cs`: finds where the text cursor is (also in browsers)
- `src/Voice.cs`: natural voices (Piper) and the Windows voice
- `src/SettingsForm.cs`, `src/Prefs.cs`: the settings window and the settings themselves
- `src/AdminMode.cs`: admin mode
- `src/Updater.cs`: updates
- `src/SoundSpell.cs`: the tray icon and everything else
- `words.txt`: everyday words, most common first
- `irish.txt`: Irish names, places and words
- `homophones.txt`: words that sound alike
- `build.ps1`, `install.ps1`, `update.ps1`, `uninstall.ps1`, `Install.cmd`: build, install, update and remove
- `VERSION`: the version number, which the updater compares
- `test/`: spelling checks (`test/run.sh` with Mono on Linux/macOS, `test/run.ps1` on Windows) and `test/e2e.ps1`, which types into Notepad on Windows to check the whole thing works

Set the environment variable `SOUNDSPELL_LOG` to a file path to get a trace of what it sees and types.
