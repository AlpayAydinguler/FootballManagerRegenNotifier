# tessdata/

`eng.traineddata` is redistributed from the [Tesseract OCR
project](https://github.com/tesseract-ocr/tessdata) under the **Apache License
2.0**, which permits redistribution. It is **not** covered by this repository's
MIT licence.

| | |
|---|---|
| Source | `https://github.com/tesseract-ocr/tessdata/raw/4.1.0/eng.traineddata` |
| Size | 23,466,654 bytes (22.4 MB) |
| SHA-256 | `daa0c97d651c19fba3b25e81317cd697e9908c8208090c94c3905381c23fc047` |

## Why the large file and not `tessdata_fast`

This is the full variant, which contains **both** the legacy character
classifier and the LSTM model. The 3.9 MB `tessdata_fast` and the 14.7 MB
`tessdata_best` builds are LSTM-only.

That matters for one specific feature. `tessedit_char_whitelist` — the
"restrict recognition to `0123456789/`" toggle — is a legacy-classifier
parameter. It was removed in Tesseract 4.0, partially restored in 4.1, and
remains merely advisory under the LSTM engine, where setting it can actually
degrade whitespace handling. It is honoured strictly only under
`EngineMode.TesseractOnly`, and that mode refuses to start against LSTM-only
language data.

So shipping the full file is what makes the whitelist toggle in the tuning panel
mean something real rather than being decorative. The application defaults to
the LSTM engine and applies the whitelist as a post-filter as well, so it still
behaves correctly if you swap this file for a smaller one — you simply lose
strict in-engine filtering.

## Replacing it

Drop any `*.traineddata` in here and it is picked up on the next run. To OCR a
non-English Football Manager, add that language's file and change the language
in the tuning panel. The application reports a clear error and keeps running if
the folder or the file is missing.
