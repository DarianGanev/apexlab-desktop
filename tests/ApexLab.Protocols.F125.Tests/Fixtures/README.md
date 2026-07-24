# Synthetic protocol fixture provenance

The F1 25 protocol tests construct their datagrams independently from the documented field and
envelope contracts. They contain no bytes captured from a real game session and no copied vendor
attachment.

Golden values are intentionally conspicuous, deterministic constants so byte order and field
boundaries remain reviewable. Random-corpus tests use fixed seeds and print the seed, sample index,
and failing bytes if an invariant is violated.
