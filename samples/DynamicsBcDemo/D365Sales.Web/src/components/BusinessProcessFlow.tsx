import { makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import { Checkmark16Regular } from '@fluentui/react-icons';
import { STAGES, stageLabel, StateCode } from '../model';

const NOTCH = '12px';

const useStyles = makeStyles({
  flow: {
    display: 'flex',
    alignItems: 'center',
    gap: '20px',
    padding: '10px 0 12px',
    borderTop: `1px solid ${tokens.colorNeutralStroke3}`,
  },
  name: {
    display: 'flex',
    flexDirection: 'column',
    minWidth: '150px',
  },
  process: {
    fontSize: tokens.fontSizeBase300,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground1,
  },
  caption: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  stages: {
    display: 'flex',
    flex: 1,
    minWidth: 0,
    margin: 0,
    padding: 0,
    listStyleType: 'none',
  },
  stage: {
    flex: 1,
    minWidth: 0,
    height: '32px',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    gap: '6px',
    paddingLeft: '20px',
    paddingRight: '16px',
    marginLeft: '-9px',
    fontSize: tokens.fontSizeBase300,
    whiteSpace: 'nowrap',
    clipPath: `polygon(0 0, calc(100% - ${NOTCH}) 0, 100% 50%, calc(100% - ${NOTCH}) 100%, 0 100%, ${NOTCH} 50%)`,
  },
  first: {
    marginLeft: 0,
    paddingLeft: '14px',
    borderTopLeftRadius: tokens.borderRadiusMedium,
    borderBottomLeftRadius: tokens.borderRadiusMedium,
    clipPath: `polygon(0 0, calc(100% - ${NOTCH}) 0, 100% 50%, calc(100% - ${NOTCH}) 100%, 0 100%)`,
  },
  completed: {
    backgroundColor: tokens.colorBrandBackground2,
    color: tokens.colorBrandForeground2,
  },
  active: {
    backgroundColor: tokens.colorBrandBackground,
    color: tokens.colorNeutralForegroundOnBrand,
    fontWeight: tokens.fontWeightSemibold,
  },
  upcoming: {
    backgroundColor: tokens.colorNeutralBackground4,
    color: tokens.colorNeutralForeground3,
  },
  dot: {
    width: '8px',
    height: '8px',
    borderRadius: tokens.borderRadiusCircular,
    border: '1.5px solid currentColor',
    boxSizing: 'border-box',
    flexShrink: 0,
  },
  activeDot: {
    backgroundColor: 'currentColor',
  },
});

interface BusinessProcessFlowProps {
  stepName: string;
  stateCode: number;
}

/** The opportunity sales process chevrons: Qualify → Develop → Propose → Close. */
export function BusinessProcessFlow({ stepName, stateCode }: BusinessProcessFlowProps) {
  const styles = useStyles();
  const won = stateCode === StateCode.WonOrQualified;
  const lost = stateCode === StateCode.LostOrDisqualified;
  const activeIndex = Math.max(0, STAGES.findIndex((stage) => stage === stepName));

  const caption = won
    ? 'Completed · won'
    : lost
      ? `Closed as lost in ${stageLabel(stepName)}`
      : `Active stage: ${stageLabel(STAGES[activeIndex])}`;

  return (
    <div className={styles.flow} data-testid="bpf">
      <div className={styles.name}>
        <span className={styles.process}>Opportunity sales process</span>
        <span className={styles.caption}>{caption}</span>
      </div>
      <ol className={styles.stages} aria-label="Business process flow">
        {STAGES.map((stage, index) => {
          const state = won || index < activeIndex ? 'completed' : index === activeIndex && !lost ? 'active' : 'upcoming';
          return (
            <li
              key={stage}
              className={mergeClasses(styles.stage, index === 0 && styles.first, styles[state])}
              aria-current={state === 'active' ? 'step' : undefined}
            >
              {state === 'completed' ? (
                <Checkmark16Regular aria-label="Completed" />
              ) : (
                <span className={mergeClasses(styles.dot, state === 'active' && styles.activeDot)} aria-hidden />
              )}
              {stageLabel(stage)}
            </li>
          );
        })}
      </ol>
    </div>
  );
}
