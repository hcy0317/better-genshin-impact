// Sandrone model provenance (not a production character capability):
//
// Formal heat rules:
// https://github.com/theBowja/genshin-db/blob/8b15995fa220c88a4d0d7ffe1e21b041d0b32588/src/data/English/talents/sandrone.json
// Maximum 100; overload exits strictly below50; firing increases power;
// cooling off-field is 300% of on-field. No heat is tied to a successful hit.
//
// Approximate measured timings from Asgater's expanded mechanism sections:
// https://www.miyoushe.com/ys/article/76348139
// 4 power/0.2s plus12/beam; C1 halves both; cooling full100 takes approximately
// 18s on-field,6s off-field,0.5s via E. At60fps these are12/1080/360/30 frames.
// The cycle tests use96f first beam (BWIKI's~1.6s), then60f between beams.
// The article reports~1.5s instead; neither is certified exact action frame data.
// https://wiki.biligame.com/ys/桑多涅/攻略 (revision678771)
//
// This package has no damage, aura, particles, passive, constellation damage,
// SDK registry, inventory catalog, or production capability declaration. These
// must be integrated and verified before claiming Sandrone is runnable.
package sandronemodel
