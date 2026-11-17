// Real-browser harness for the P13 network scene: the same module the TGUI window mounts.
// It is bundled by scripts/test-netdm-scene-browser.ps1 and rendered by headless Chromium.
import { createScene } from '../packages/tgui/interfaces/NetdmTopology/scene';
import { parseView } from '../packages/tgui/interfaces/NetdmTopology/model';

const host = document.getElementById('host') as HTMLElement;
const view = parseView({
  nodes: [['1', 10, 10, 16], ['2', 16, 10, 1], ['3', 22, 12, 1], ['4', 28, 10, 1]],
  edges: [['1', '2'], ['2', '3'], ['3', '4']],
});
createScene(host, {
  onSelect: () => undefined,
  onFailure: (reason: string) => { document.title = 'HARNESS_FAIL:' + reason; },
}, () => false)
  .then((scene) => {
    scene.update(view, 'station-1');
    scene.setFilters({ radio: true, devices: true });
    scene.setOtherPresences({ '3': 2 });
    scene.setActivity({ '2': 2, '3': 1, '4': 3 });
    scene.setAgents([
      { node: '2', goal: 'sentinel', label: 'sentinel' },
      { node: '4', goal: 'intruder', label: 'rogue' },
    ]);
    scene.setSelection('2');
    scene.setPresence('1');
    scene.setRoute({ nodes: ['1', '2', '3'], radioLegs: [], moving: false });
    scene.fit();
    document.title = 'HARNESS_OK:' + scene.backend;
  })
  .catch(() => { document.title = 'HARNESS_ERROR'; });
