import { createLightTheme, type BrandVariants, type Theme } from '@fluentui/react-components';

// Teal, so the look-alike reads as Business Central and never as the blue Sales Hub app.
// 10 is the darkest step, 160 the lightest; 80 is the primary brand colour (4.8:1 on white).
const teal: BrandVariants = {
  10: '#020D0C',
  20: '#07201E',
  30: '#0A302D',
  40: '#0B3E3B',
  50: '#0B4D49',
  60: '#0A5D58',
  70: '#086E68',
  80: '#057F78',
  90: '#2B918A',
  100: '#48A29C',
  110: '#63B3AD',
  120: '#7EC3BE',
  130: '#99D2CE',
  140: '#B5E0DD',
  150: '#D1EDEB',
  160: '#ECF8F7',
};

// Teams-like purple for the simulated #integration-alerts channel.
const purple: BrandVariants = {
  10: '#0A0A1A',
  20: '#16163A',
  30: '#222256',
  40: '#2D2E6E',
  50: '#383A85',
  60: '#44479A',
  70: '#4F52B2',
  80: '#5B5FC7',
  90: '#6F73D2',
  100: '#8387DB',
  110: '#979BE3',
  120: '#ABAEEB',
  130: '#BFC1F1',
  140: '#D2D4F6',
  150: '#E5E6FA',
  160: '#F4F4FC',
};

export const bcTheme: Theme = createLightTheme(teal);

export const teamsTheme: Theme = createLightTheme(purple);

/** Colours of the Business Central chrome that have no Fluent token. */
export const chrome = {
  headerBackground: '#10292B',
  headerForeground: '#FFFFFF',
  headerMuted: 'rgba(255, 255, 255, 0.72)',
  pageBackground: '#F4F6F6',
  cue: teal[80],
  cueHover: teal[70],
  cueUnfavorable: '#B3263E',
  cueUnfavorableHover: '#9A1F35',
  brandTint: teal[160],
  brandTintStrong: teal[150],
  brandText: teal[70],
};

/** Colours of the simulated Teams channel. */
export const teams = {
  header: '#464775',
  headerMuted: 'rgba(255, 255, 255, 0.78)',
  page: '#F5F5F5',
  critical: '#C4314B',
  error: '#E3582B',
  warning: '#E9A800',
  information: '#2F6FD0',
};
