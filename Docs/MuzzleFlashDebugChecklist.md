## Legion CE muzzle flash debug checklist

Use a dark-night map with a fixed firing line.

### Weapons to test

- `LG_M16A1C` or another AR
- `LG_SpringField` or `LG_AMR`
- `LG_Colt` or `LG_OldRev`
- `LG_LawShotgun` or `LG_SpecOpsShotgun`

### Per-weapon checks

1. Fire one shot in isolation.
2. Confirm original Legion muzzle flash appears.
3. Check `Player.log` for:
   - `Flash map <weapon>` at startup
   - `CE lighting notified for <weapon>` on shot
   - `Missing legacy flash mapping for <weapon>` must not appear
4. Compare whether CE night flash is visible across categories from the same camera zoom.

### Interpretation

- If `CE lighting notified` appears and no CE night flash is visible:
  visual reproduction is failing, not mapping.
- If `Missing legacy flash mapping` appears:
  lookup/registration is failing for that weapon.
- If neither appears:
  the weapon did not pass through the current Legion CE hook path.
