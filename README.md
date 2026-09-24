# SoundSpell

A small Windows helper for people who spell words the way they sound, made with dyslexia in mind.

In any app (Word, a browser, Discord, Notepad), type `@@` and then the word as it sounds, then a space:

| You type        | You get       |
|-----------------|---------------|
| `@@nesesary `   | `necessary `  |
| `@@sykology `   | `psychology ` |
| `@@wensday `    | `Wednesday `  |
| `@@freind `     | `friend `     |
| `@@becuz `      | `because `    |
| `@@Restront `   | `Restaurant ` |

Matches pop up next to the text **while you are still typing** the word:

- **Space** or punctuation puts in the first match
- **Enter** (or Tab) puts in the first match **and waits**, so a chat message is not sent yet. Press Enter again to send.
- **Ctrl+1 … Ctrl+5** puts in that match instead
- **Ctrl+0** (after a fix) puts back what you typed

The popup closes as soon as you keep typing, clicks, or switches window.

Capitals carry over (`@@THRU` → `THROUGH`).

SoundSpell sits with the hidden icons by the clock (the **^** arrow). Right-click it for:

- **On**: switch it off and on
- **Replace the word automatically**: turn this off to only see suggestions, then Ctrl+number puts one in (Enter waits so a message is not sent too early)
- **Read the word out loud**: hear each fixed word, to tell it is the right one
- **My words…**: names, places and school words, one per line. They are found first.
- **Try it…**: a window to type spellings and see or hear every match (double-clicking the tray icon opens it too)

## Install (a few seconds, no admin rights)

1. Download this repository as a zip (**Code → Download ZIP**), or the **SoundSpell** zip from the latest *build* GitHub Actions run, which has the program already built.
2. Double-click **`Install.cmd`**.

It is running now, and it starts with Windows from here on. It builds itself with the C# compiler that is already part of Windows 10 and 11, so nothing is downloaded. If the zip already has `SoundSpell.exe`, that file is used.

If Windows shows *"Windows protected your PC"*, choose **More info → Run anyway** (the installer is not signed).

To uninstall: **Settings → Apps → SoundSpell → Uninstall**. The *My words* list in `%APPDATA%\SoundSpell` is kept.

## How it finds the word

Every dictionary word (50,000 common English words) gets a "sound key": `ph` and `f` sound the same, so do `c`/`k`/`q`, `tion`/`shun`, silent letters (`kn`, `wr`, `could`) are dropped, and all vowel sounds count as one. The typed word is matched on sound first, then spelling, then how common the word is. Mix-ups that are common with dyslexia cost less: flipped letters (`b`/`d`, `p`/`q`), swapped neighbours (`freind`), and dropped vowels. Some words don't sound like their spelling (`Wednesday`, `receipt`, `colonel`), so those also get the way they are said.

## Files

- `src/Speller.cs`: the matcher (plain C#, no Windows parts)
- `src/SoundSpell.cs`: the tray app, the keyboard watcher and the popup
- `words.txt`: the word list, most common first
- `build.ps1`, `install.ps1`, `uninstall.ps1`, `Install.cmd`: build and install
- `test/`: spelling checks (`test/run.sh` with Mono on Linux/macOS, `test/run.ps1` on Windows) and `test/e2e.ps1`, which types into Notepad on Windows to check the whole thing works

Set the environment variable `SOUNDSPELL_LOG` to a file path to get a trace of what it sees and types.
