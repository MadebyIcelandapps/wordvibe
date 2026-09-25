#!/bin/sh
# Compiles the speller as C# 5 (what Windows' built-in csc accepts) and runs the checks.
set -e
cd "$(dirname "$0")/.."
mcs -langversion:5 -nologo -out:test/spelltest.exe src/Speller.cs src/Homophones.cs test/SpellTest.cs
mono test/spelltest.exe words.txt
