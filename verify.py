# Fixture-only check for the desktop status board.
# Run: python verify.py
# No GPU, no Windows event log, no network. Hardware reads stay inside fixtures/.
import datetime as dt
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import traceback
from xml.etree import ElementTree

import psutil
from PIL import Image

import desktop_status as board

ROOT = os.path.dirname(os.path.abspath(__file__))
ART = os.path.join(ROOT, '.cursor', 'skills', 'verify-desktop-status', 'artifacts')
NS = {'t': 'http://schemas.microsoft.com/windows/2004/02/mit/task'}
DASHES = ('\u2013', '\u2014')
LIBRE = 'Libre' + 'HardwareMonitor'
HOME_LITERAL = re.compile(r'(?i)[A-Za-z]:[\\/]+users[\\/]+')
SKIP_DIRS = {'.git', '__pycache__', 'artifacts', '.venv', 'runtime'}


def main(argv):
    if '--cleanup' in argv:
        cleanup()
        print('Cleanup removed repo runtime scratch. Proof artifacts were left in place.')
        return 0
    if '--doctor' in argv:
        doctor()
        return 0
    os.chdir(ROOT)
    steps = [
        ('source is free of hardcoded home paths and dashes', check_source),
        ('requirements are Pillow and psutil only', check_requirements),
        ('thresholds and event window', check_thresholds),
        ('fan, disk, memory, and stability rules', check_rules),
        ('task XML triggers and backup', check_task_xml),
        ('doctor', check_doctor),
        ('healthy board end to end', lambda: check_board('healthy', expect_healthy)),
        ('attention board end to end', lambda: check_board('attention', expect_attention)),
        ('missing readings stay grey', lambda: check_board('missing', expect_missing)),
        ('stale sensor asks the reader to start', check_stale_sensor),
        ('slow reads are cached', check_cache),
        ('stop note pauses a program', check_stop_note),
        ('access denied asks for an elevated PowerShell', check_access_denied),
        ('overlay glass, pill, and cheap redraw', check_overlay),
    ]
    for name, fn in steps:
        print(f'RUN {name}', flush=True)
        fn()
        print(f'PASS {name}')
    print('VERIFY OK')
    return 0


def doctor():
    healthy = os.path.join(ROOT, 'fixtures', 'healthy', 'board.json')
    if not os.path.isfile(healthy):
        raise SystemExit('DOCTOR FAIL missing fixtures/healthy/board.json')
    text = open(os.path.join(ROOT, 'desktop_status.py'), encoding='utf-8').read()
    if HOME_LITERAL.search(text):
        raise SystemExit('DOCTOR FAIL desktop_status.py has a hardcoded home path')
    if LIBRE.lower() in text.lower():
        raise SystemExit('DOCTOR FAIL desktop_status.py names a forbidden monitor')
    print(f'python {sys.version.split()[0]}')
    print(f'pillow {Image.__version__}')
    print(f'psutil {psutil.__version__}')
    print(f'script {ROOT}')
    print(f'fixtures {os.path.join(ROOT, "fixtures")}')
    print('hardware skipped')
    print('DOCTOR OK')


def cleanup():
    runtime = os.path.join(ROOT, 'runtime')
    if os.path.isdir(runtime):
        shutil.rmtree(runtime)


def check_source():
    bad_hits = []
    for dirpath, dirnames, filenames in os.walk(ROOT):
        dirnames[:] = [name for name in dirnames if name not in SKIP_DIRS and name != 'artifacts']
        for name in filenames:
            path = os.path.join(dirpath, name)
            if os.path.splitext(name)[1].lower() not in {'.py', '.ps1', '.vbs', '.md', '.json', '.csv', '.txt', '.log', ''}:
                if name not in {'.gitignore'}:
                    continue
            try:
                text = open(path, encoding='utf-8').read()
            except UnicodeDecodeError:
                continue
            rel = os.path.relpath(path, ROOT)
            lowered = text.lower()
            if LIBRE.lower() in lowered:
                bad_hits.append(rel + ' has a forbidden token')
            if HOME_LITERAL.search(text):
                bad_hits.append(rel + ' has a hardcoded home path')
            for dash in DASHES:
                if dash in text:
                    bad_hits.append(rel + ' has an en dash or em dash')
    if bad_hits:
        raise AssertionError('; '.join(bad_hits))
    vbs = open(os.path.join(ROOT, 'run-desktop-status.vbs'), encoding='utf-8').read()
    for needle in ('WScript.ScriptFullName', '.venv', 'pythonw.exe', 'desktop_status.py', 'runtime'):
        if needle not in vbs:
            raise AssertionError('vbs missing ' + needle)
    ps1 = open(os.path.join(ROOT, 'register-desktop-status.ps1'), encoding='utf-8').read()
    for needle in ('MyInvocation', 'LogonTrigger', 'TimeTrigger', 'P3650D', 'PT2M', 'StopAtDurationEnd', 'IgnoreNew', 'StartWhenAvailable', 'LeastPrivilege', 'InteractiveToken', 'task-backups', '-Force'):
        if needle not in ps1:
            raise AssertionError('register script missing ' + needle)
    ignore = open(os.path.join(ROOT, '.gitignore'), encoding='utf-8').read()
    if 'runtime/' not in ignore:
        raise AssertionError('runtime folder is not gitignored')
    saved = {key: os.environ.get(key) for key in ('DEG_RUNTIME_DIR', 'DEG_DESK_ROOT', 'DEG_EDGE_PAPER', 'DEG_SENSOR_FILE')}
    for key in saved:
        os.environ.pop(key, None)
    try:
        paths = board.resolve_paths()
    finally:
        for key, value in saved.items():
            if value is None:
                os.environ.pop(key, None)
            else:
                os.environ[key] = value
    if paths.runtime != os.path.join(board.script_dir(), 'runtime'):
        raise AssertionError('runtime path is not next to the script')
    if not paths.sensor.endswith(os.path.join('DEG', 'cpu-sensor', 'out', 'last.json')):
        raise AssertionError('sensor default is not the PawnIO output file')
    probe = 'C:' + '\\' + 'Users' + '\\' + 'someone\\Desktop'
    if not HOME_LITERAL.search(probe):
        raise AssertionError('home path pattern missed a users folder')
    if HOME_LITERAL.search(os.path.join('ProgramData', 'DEG', 'cpu-sensor')):
        raise AssertionError('home path pattern matched a normal path')
    if board.render_size() != (3440, 1440):
        raise AssertionError('default wallpaper size changed')
    if board.LiveHost.built != 0:
        raise AssertionError('importing the board touched the live host')


def check_requirements():
    lines = [line.strip() for line in open(os.path.join(ROOT, 'requirements.txt'), encoding='utf-8') if line.strip() and not line.startswith('#')]
    if lines != ['Pillow>=10.4', 'psutil>=5.9']:
        raise AssertionError('requirements.txt should list only Pillow and psutil, found ' + repr(lines))


def check_thresholds():
    expect = {
        'cpu_cool_c': 60, 'cpu_warm_c': 75, 'cpu_hot_c': 90, 'cpu_vcore_warn_v': 1.5,
        'gpu_cool_c': 65, 'gpu_warm_c': 80, 'gpu_hot_c': 88, 'gpu_power_over_limit_ratio': 1.05,
        'mem_warn_pct': 80, 'mem_bad_pct': 92, 'ram_voltage_warn_v': 1.45,
        'disk_free_ok_pct': 15, 'disk_free_warn_pct': 8,
        'disk_wear_warn_pct': 80, 'disk_wear_bad_pct': 95,
        'disk_temp_warn_c': 55, 'disk_temp_bad_c': 70,
        'fan_stop_warn_cpu_c': 70, 'fan_min_rpm': 100,
        'event_window_days': 30, 'app_crash_bad_count': 5, 'uptime_warn_days': 30,
        'chip_microcode_min': 0x133,
    }
    for key, value in expect.items():
        if board.THRESHOLDS[key] != value:
            raise AssertionError(f'{key} is {board.THRESHOLDS[key]!r}, expected {value!r}')
    if '2592000000' not in board.xpath_whea():
        raise AssertionError('WHEA query is not a 30 day window')
    for name, text in (
        ('whea', board.xpath_whea()),
        ('power', board.xpath_kernel_power()),
        ('disk', board.xpath_disk()),
        ('crash', board.xpath_crash()),
    ):
        if '2592000000' not in text:
            raise AssertionError(name + ' is not a 30 day query')
        if '604800000' in text:
            raise AssertionError(name + ' is still a 7 day query')
    if "EventID=41" not in board.xpath_kernel_power():
        raise AssertionError('kernel power query lost event 41')
    if 'EventID=153' not in board.xpath_disk() or 'EventID=157' not in board.xpath_disk():
        raise AssertionError('disk query lost an event id')
    if 'Application Error' not in board.xpath_crash() or 'EventID=1000' not in board.xpath_crash():
        raise AssertionError('crash query lost application errors')


def check_rules():
    now = dt.datetime(2026, 10, 9, 18, 0, 0)

    def cpu(temp, vcore=1.1):
        snap = {'now': now, 'sensor': {'data': {
            'cpu_temp_c': temp, 'cpu_pkg_power_w': 30, 'cpu_core_temps_c': [temp],
            'tjmax_c': 100, 'cpu_vcore_v': vcore, 'diag': {'pl1_w': 125, 'pl2_w': 253},
        }, 'age': 30, 'started': False}}
        return board.cpu_row(snap)

    assert_level(cpu(59.9), 'ok', 'Cool')
    assert_level(cpu(60), 'ok', 'Warm')
    assert_level(cpu(74.9), 'ok', 'Warm')
    assert_level(cpu(75), 'warn', 'Hot')
    assert_level(cpu(89.9), 'warn', 'Hot')
    assert_level(cpu(90), 'bad', 'Too hot')
    high = cpu(50, 1.51)
    assert_level(high, 'warn', 'voltage high')

    def fans(rpm, temp):
        snap = {'sensor': {'data': {'cpu_temp_c': temp, 'fans': [{'name': 'CPU fan', 'rpm': rpm}]}}, 'fans_wmi': [], 'fans_wmi_error': None}
        return board.fan_row(snap)

    assert_level(fans(0, 69), 'ok', '0 rpm')
    assert_level(fans(0, 70), 'bad', 'while the processor is warm')
    assert_level(fans(100, 90), 'ok', '100 rpm')
    assert_level(fans(99, 90), 'bad', 'while the processor is warm')
    missing = board.fan_row({'sensor': {'data': {'cpu_temp_c': 40}}, 'fans_wmi': [{'Name': 'Fan', 'DesiredSpeed': 0}], 'fans_wmi_error': None})
    assert_level(missing, 'na', 'no fan speed in the CPU sensor file or Windows fan list')
    if board.fans_from_sensor({'cpu_fan_rpm': 840})[0]['rpm'] != 840:
        raise AssertionError('flat sensor fan key was ignored')
    named = {item['name']: item['rpm'] for item in board.fans_from_sensor({'fans': {'CPU': 800, 'Case': 600}})}
    if named != {'CPU': 800, 'Case': 600}:
        raise AssertionError('fan map was not read')
    if board.fans_from_wmi([{'Name': 'Fan', 'DesiredSpeed': 0}]):
        raise AssertionError('a Windows fan reading of 0 was treated as a real speed')

    def gpu(temp, power=20, limit=320):
        parsed = {
            'name': 'Card', 'temp_c': temp, 'util_pct': 0, 'mem_used_mib': 100, 'mem_total_mib': 1024,
            'power_w': power, 'power_limit_w': limit, 'fan_pct': 10, 'clock_gr': 200, 'clock_mem': 400,
            'clock_sm': 200, 'clock_max_gr': 2000, 'clock_max_mem': 8000, 'voltage_v': None,
            'voltage_reason': 'nvidia-smi did not report GPU voltage',
        }
        return board.gpu_row({'gpu': parsed})

    assert_level(gpu(64.9), 'ok', 'Cool')
    assert_level(gpu(80), 'warn', 'Hot')
    assert_level(gpu(88), 'bad', 'Too hot')
    over = gpu(40, 400, 320)
    assert_level(over, 'warn', 'over its power limit')
    if 'Voltage not available' not in ' '.join(over['more']):
        raise AssertionError('missing GPU voltage did not explain itself')

    def memory(percent):
        total = 64 * 2 ** 30
        return board.mem_row({'memory': {'used': total * percent / 100, 'total': total, 'available': total * (1 - percent / 100), 'percent': percent}})

    assert_level(memory(79.9), 'ok', 'comfortable')
    assert_level(memory(80), 'warn', 'getting full')
    assert_level(memory(92), 'bad', 'nearly full')

    def speed(configured, rated, mv):
        return board.ram_speed_row({'modules': [{'BankLabel': 'A', 'ConfiguredClockSpeed': configured, 'Speed': rated, 'ConfiguredVoltage': mv}]})

    assert_level(speed(5600, 4800, 1100), 'ok', '5600 MT/s, 1.10 V')
    assert_level(speed(0, 4800, 0), 'ok', '4800 MT/s. Voltage not available: Windows did not report memory voltage')
    assert_level(speed(5600, 5600, 1460), 'warn', 'voltage high')
    assert_level(board.ram_speed_row({'modules': []}), 'na', 'Windows did not report memory speed')

    def space(pct_free):
        total = 1000 * 2 ** 30
        free = total * pct_free / 100
        return board.disk_row({'disk_c': {'free': free, 'total': total}})

    assert_level(space(15.1), 'ok', None)
    assert_level(space(15), 'warn', None)
    assert_level(space(8.1), 'warn', None)
    assert_level(space(8), 'bad', None)

    def disks(wear=2, health='Healthy', temp=34, bad_reads=0):
        return board.disk_health_row({'disk_health': {
            'disks': [{'DeviceId': 0, 'FriendlyName': 'Drive', 'HealthStatus': health, 'OperationalStatus': 'OK'}],
            'reliability': [{'DeviceId': '0', 'Temperature': temp, 'Wear': wear, 'ReadErrorsUncorrected': bad_reads, 'WriteErrorsUncorrected': 0}],
        }})

    assert_level(disks(79), 'ok', 'All disks healthy')
    assert_level(disks(80), 'warn', None)
    assert_level(disks(95), 'bad', None)
    assert_level(disks(temp=55), 'warn', None)
    assert_level(disks(temp=70), 'bad', None)
    assert_level(disks(bad_reads=1), 'bad', None)
    assert_level(board.disk_health_row({'disk_health': {'error': 'Windows did not return disk health counters'}}), 'na', 'Windows did not return disk health counters')

    def events(whea=0, kp=0, disk=0, crash=0):
        return board.stability_row({'events': {'whea': whea, 'kp': kp, 'disk': disk, 'crash': crash}})

    assert_level(events(), 'ok', 'No unexpected restarts in 30 days')
    assert_level(events(kp=1), 'bad', '1 unexpected restart in the last 30 days')
    assert_level(events(crash=5), 'warn', '5 application crashes')
    assert_level(events(crash=6), 'bad', '6 application crashes')
    assert_level(board.stability_row({'events': {'error': 'the Windows event log could not be read'}}), 'na', 'the Windows event log could not be read')


def assert_level(item, level, text):
    if item['level'] != level:
        raise AssertionError(f"{item['label']} level {item['level']} != {level}: {item['text']}")
    if text and text not in item['text'] and text not in ' '.join(str(part) for part in item['more']):
        raise AssertionError(f"{item['label']} missing {text!r} in {item['text']!r}")


def check_task_xml():
    first = tempfile.mkdtemp(prefix='task-xml-')
    try:
        out1 = os.path.join(first, 'task.xml')
        out2 = os.path.join(first, 'task-again.xml')
        old = os.path.join(first, 'old.xml')
        backup = os.path.join(first, 'backup')
        open(old, 'w', encoding='utf-8').write('<Task>OLD-MARKER previous definition</Task>\n')
        emit(out1, user='FIXTURE\\boarduser', when='2026-10-09T18:00:00')
        emit(out2, user='FIXTURE\\boarduser', when='2026-10-09T18:00:00')
        if open(out1, encoding='utf-8').read() != open(out2, encoding='utf-8').read():
            raise AssertionError('emitting the task twice did not match')
        emit(out2, user='FIXTURE\\boarduser', when='2026-10-09T18:00:00', existing=old, backup=backup)
        if open(out1, encoding='utf-8').read() != open(out2, encoding='utf-8').read():
            raise AssertionError('re-register changed the new task definition')
        backups = [name for name in os.listdir(backup) if name.endswith('.xml')]
        if len(backups) != 1:
            raise AssertionError('old task was not backed up once')
        saved = open(os.path.join(backup, backups[0]), encoding='utf-8').read()
        if 'OLD-MARKER' not in saved:
            raise AssertionError('backup is not the previous task')
        fresh = open(out2, encoding='utf-8').read()
        if 'OLD-MARKER' in fresh or HOME_LITERAL.search(fresh):
            raise AssertionError('new task XML kept old or hardcoded contents')
        assert_task_shape(out2, 'FIXTURE\\boarduser', '2026-10-09T18:01:00')
    finally:
        shutil.rmtree(first)


def powershell_exe():
    found = shutil.which('pwsh')
    if found:
        return found
    if os.name == 'nt':
        for name in ('powershell.exe', 'powershell'):
            found = shutil.which(name)
            if found:
                return found
    raise AssertionError('neither pwsh nor powershell.exe is on PATH')


def emit(dest, user, when, existing=None, backup=None):
    cmd = [
        powershell_exe(), '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
        os.path.join(ROOT, 'register-desktop-status.ps1'),
        '-EmitXml', dest, '-UserId', user, '-RegisteredAt', when,
    ]
    if existing:
        cmd.extend(['-ExistingXml', existing])
    if backup:
        cmd.extend(['-BackupDir', backup])
    proc = subprocess.run(cmd, capture_output=True, text=True, encoding='utf-8', errors='replace')
    if proc.returncode != 0:
        raise AssertionError('register script failed\n' + (proc.stdout or '') + (proc.stderr or ''))


def assert_task_shape(path, user, start):
    root = ElementTree.parse(path).getroot()
    logons = root.findall('t:Triggers/t:LogonTrigger', NS)
    times = root.findall('t:Triggers/t:TimeTrigger', NS)
    if len(logons) != 1 or len(times) != 1:
        raise AssertionError(f'expected one logon and one time trigger, got {len(logons)} and {len(times)}')
    if root.findall('t:Triggers/t:CalendarTrigger', NS):
        raise AssertionError('unexpected calendar trigger')
    if logons[0].find('t:Repetition', NS) is not None:
        raise AssertionError('logon trigger still carries a repetition')
    if text_of(logons[0], 't:UserId') != user or text_of(logons[0], 't:Enabled') != 'true':
        raise AssertionError('logon trigger user or enabled flag is wrong')
    rep = times[0].find('t:Repetition', NS)
    if text_of(rep, 't:Interval') != 'PT2M' or text_of(rep, 't:Duration') != 'P3650D' or text_of(rep, 't:StopAtDurationEnd') != 'false':
        raise AssertionError('time trigger repetition is not every 2 minutes for 3650 days with StopAtDurationEnd false')
    if text_of(times[0], 't:StartBoundary') != start or text_of(times[0], 't:Enabled') != 'true':
        raise AssertionError('time trigger does not start one minute after registration')
    settings = root.find('t:Settings', NS)
    for tag, value in (
        ('t:MultipleInstancesPolicy', 'IgnoreNew'),
        ('t:StartWhenAvailable', 'true'),
        ('t:Hidden', 'true'),
        ('t:ExecutionTimeLimit', 'PT0S'),
        ('t:DisallowStartIfOnBatteries', 'false'),
        ('t:StopIfGoingOnBatteries', 'false'),
    ):
        if text_of(settings, tag) != value:
            raise AssertionError(f'{tag} is {text_of(settings, tag)!r}, expected {value!r}')
    principal = root.find('t:Principals/t:Principal', NS)
    if text_of(principal, 't:UserId') != user:
        raise AssertionError('principal is not the interactive user')
    if text_of(principal, 't:LogonType') != 'InteractiveToken' or text_of(principal, 't:RunLevel') != 'LeastPrivilege':
        raise AssertionError('principal is not a limited interactive user')
    action = root.find('t:Actions/t:Exec', NS)
    if text_of(action, 't:Command') != 'wscript.exe':
        raise AssertionError('action is not wscript')
    if 'run-desktop-status.vbs' not in text_of(action, 't:Arguments'):
        raise AssertionError('action does not launch the vbs next to the script')
    work = text_of(action, 't:WorkingDirectory')
    if os.path.abspath(work) != ROOT:
        raise AssertionError('working directory is not the script folder')


def text_of(elem, path):
    found = elem.find(path, NS)
    if found is None or found.text is None:
        return ''
    return found.text.strip()


def check_doctor():
    proc = subprocess.run([sys.executable, os.path.join(ROOT, 'desktop_status.py'), '--doctor'], capture_output=True, text=True)
    if proc.returncode != 0 or 'DOCTOR OK' not in proc.stdout:
        raise AssertionError('doctor failed\n' + proc.stdout + proc.stderr)
    if os.path.exists(os.path.join(ROOT, 'status.html')):
        raise AssertionError('doctor wrote a status page')


def check_board(name, expect):
    runtime = os.path.join(ART, name + '-board')
    if os.path.isdir(runtime):
        shutil.rmtree(runtime)
    os.makedirs(ART, exist_ok=True)
    env = os.environ.copy()
    for key in ('DEG_DESK_ROOT', 'DEG_EDGE_PAPER', 'DEG_SENSOR_FILE', 'DEG_RUNTIME_DIR', 'DEG_RENDER_SIZE', 'DEG_RENDER_BLUR'):
        env.pop(key, None)
    env['DEG_RUNTIME_DIR'] = runtime
    proc = subprocess.run(
        [sys.executable, os.path.join(ROOT, 'desktop_status.py'), '--fixture', os.path.join('fixtures', name), '--no-set'],
        cwd=ROOT, env=env, capture_output=True, text=True,
    )
    if proc.returncode != 0:
        raise AssertionError(name + ' board failed\n' + proc.stdout + proc.stderr)
    if 'Wallpaper not changed (fixture)' not in proc.stdout:
        raise AssertionError(name + ' tried to change the wallpaper or skipped the fixture note')
    html_path = os.path.join(runtime, 'status.html')
    pngs = [os.path.join(runtime, 'render', name) for name in ('wallpaper-a.png', 'wallpaper-b.png')]
    pngs = [path for path in pngs if os.path.isfile(path)]
    if not os.path.isfile(html_path) or not pngs or not os.path.isfile(os.path.join(runtime, 'latest-render.png')):
        raise AssertionError(name + ' did not write runtime html and png')
    if os.path.exists(os.path.join(ROOT, 'status.html')) or os.path.exists(os.path.join(ROOT, 'latest-render.png')) or os.path.exists(os.path.join(ROOT, 'state.json')):
        raise AssertionError('board wrote html or state next to the script')
    raw = open(html_path, 'rb').read()
    if not raw.startswith(b'\xef\xbb\xbf'):
        raise AssertionError(name + ' html has no utf-8 BOM')
    if b'charset="utf-8"' not in raw:
        raise AssertionError(name + ' html has no charset tag')
    if name in ('healthy', 'attention') and b'\xc2\xb0' not in raw:
        raise AssertionError(name + ' html lost the degree sign')
    page = raw.decode('utf-8-sig')
    if page.count('<div class="r">') != page.count('>Source: ') or '>Source: ' not in page:
        raise AssertionError(name + ' detail page is missing a source on a row')
    for dash in DASHES:
        if dash in page:
            raise AssertionError(name + ' page contains an en dash or em dash')
    commands = json.load(open(os.path.join(runtime, 'commands.json'), encoding='utf-8'))
    if commands:
        raise AssertionError(name + ' fixture ran commands: ' + json.dumps(commands))
    image = Image.open(pngs[0])
    if image.size != (3440, 1440):
        raise AssertionError(f'{name} png is {image.size}, expected 3440x1440')
    expect(page, proc.stdout, image)
    log = os.path.join(ART, name + '-stdout.txt')
    open(log, 'w', encoding='utf-8').write(proc.stdout)


def expect_healthy(page, stdout, image):
    if 'Board: All good' not in stdout or 'readings we have' in heading(page):
        raise AssertionError('healthy headline was ' + heading(page))
    if '#4ade80' not in heading(page):
        raise AssertionError('healthy headline is not green')
    for phrase in (
        'FIXTURE-PC', 'Cool, 48', 'RTX 4080', '0.875 V', '210 MHz', '22 W',
        'CPU fan 840 rpm, Case fan 610 rpm', '20 of 64 GB used (plenty free)',
        '5600 MT/s, 1.10 V', '400 GB free of 1000 GB', 'All disks healthy',
        'No unexpected restarts in 30 days', 'Intel fix installed',
        'Up to date, nothing waiting', 'On for 2 days, 3 hr',
        'Fake money: up $12.50 on 4 practice trades',
        'Not enough data yet (12 trades so far)', 'Passed (40 trades)',
        'Working now: benchmark script', 'Running, listening for messages',
        'Chrome 12%', 'Cursor 1.7 GB', 'data fresh', 'Idle, last ran 10 min ago',
    ):
        if phrase not in page:
            raise AssertionError('healthy page missing ' + phrase)
    if 'Not available:' in page:
        raise AssertionError('healthy page has a missing reading')
    if not has_color(image, (74, 222, 128)):
        raise AssertionError('healthy wallpaper has no green dot')


def expect_attention(page, stdout, image):
    if 'Needs a look' not in heading(page) or '#f87171' not in heading(page):
        raise AssertionError('attention headline was ' + heading(page))
    for phrase in (
        'Too hot, 92', 'voltage high', 'Too hot, 90', 'over its power limit',
        'CPU fan reads 0 rpm while the processor is warm', 'nearly full',
        '1.50 V', '50 GB free', 'Unhealthy', '2 unexpected restarts',
        'in the last 30 days', 'Intel fix may be missing', '40 days',
    ):
        if phrase not in page:
            raise AssertionError('attention page missing ' + phrase)
    if not has_color(image, (248, 113, 113)):
        raise AssertionError('attention wallpaper has no red dot')


def expect_missing(page, stdout, image):
    if heading(page).find('All good on the readings we have') < 0:
        raise AssertionError('missing headline was ' + heading(page))
    for phrase in (
        'Not available: no fan speed in the CPU sensor file or Windows fan list',
        'Not available: Windows did not return disk health counters',
        'Not available: the Windows event log could not be read',
        'Voltage not available: nvidia-smi did not report GPU voltage',
        'Voltage not available: Windows did not report memory voltage',
        '5600 MT/s',
    ):
        if phrase not in page:
            raise AssertionError('missing page missing ' + phrase)
    if '#949caa' not in page:
        raise AssertionError('missing readings are not grey')
    if 'Needs a look' in heading(page):
        raise AssertionError('a missing reading was treated as a failure')


def heading(page):
    start = page.find('<h1>')
    end = page.find('</h1>')
    return page[start:end]


def has_color(image, rgb):
    pixels = image.load()
    width, height = image.size
    for y in range(0, height, 2):
        for x in range(width // 2, width, 2):
            red, green, blue = pixels[x, y][:3]
            if abs(red - rgb[0]) <= 50 and abs(green - rgb[1]) <= 50 and abs(blue - rgb[2]) <= 50:
                return True
    return False


def check_stale_sensor():
    folder = tempfile.mkdtemp(prefix='stale-sensor-')
    try:
        shutil.copytree(os.path.join(ROOT, 'fixtures', 'healthy'), folder, dirs_exist_ok=True)
        sensor = json.load(open(os.path.join(folder, 'sensor.json'), encoding='utf-8'))
        sensor['ts'] = '2026-10-09T10:00:00'
        json.dump(sensor, open(os.path.join(folder, 'sensor.json'), 'w', encoding='utf-8'))
        board.apply_fixture_env(folder)
        host = board.FixtureHost(folder, json.load(open(os.path.join(folder, 'board.json'), encoding='utf-8')))
        paths = board.resolve_paths()
        snap = board.load_snapshot(host, paths, {})
        if not host.commands or host.commands[0][:3] != ['schtasks', '/run', '/tn']:
            raise AssertionError('stale sensor did not start the reader: ' + json.dumps(host.commands))
        if host.commands[0][3] != r'\DEG\DEG-CpuSensorPawnIO':
            raise AssertionError('sensor kick targeted the wrong task')
        row = board.cpu_row(snap)
        if row['level'] != 'info' or 'restarting the sensor' not in row['text']:
            raise AssertionError('stale processor line was ' + row['text'])
    finally:
        shutil.rmtree(folder)


def check_cache():
    folder = os.path.join(ROOT, 'fixtures', 'healthy')
    board.apply_fixture_env(folder)
    host = board.FixtureHost(folder, json.load(open(os.path.join(folder, 'board.json'), encoding='utf-8')))
    calls = {'n': 0}
    original = host.event_count

    def wrapped(log, xpath):
        calls['n'] += 1
        return original(log, xpath)

    host.event_count = wrapped
    paths = board.resolve_paths()
    state = {}
    board.load_snapshot(host, paths, state)
    board.load_snapshot(host, paths, state)
    if calls['n'] != 4:
        raise AssertionError(f'event log was read {calls["n"]} times, expected 4 across two renders')


def check_stop_note():
    folder = tempfile.mkdtemp(prefix='stop-note-')
    try:
        shutil.copytree(os.path.join(ROOT, 'fixtures', 'healthy'), folder, dirs_exist_ok=True)
        note = os.path.join(folder, 'edge-paper', 'STOPPED.txt')
        open(note, 'w', encoding='utf-8').write('paused\n')
        board.apply_fixture_env(folder)
        host = board.FixtureHost(folder, json.load(open(os.path.join(folder, 'board.json'), encoding='utf-8')))
        snap = board.load_snapshot(host, board.resolve_paths(), {})
        rows = board.tasks_rows(snap)
        edge = [item for item in rows if item['label'] == 'Edge Paper']
        if len(edge) != 1 or edge[0]['level'] != 'off' or 'Paused on purpose' not in edge[0]['text']:
            raise AssertionError('stop note did not pause Edge Paper')
    finally:
        shutil.rmtree(folder)


def check_access_denied():
    script = open(os.path.join(ROOT, 'register-desktop-status.ps1'), encoding='utf-8').read()
    line = 'Access is denied. Run this script from an elevated PowerShell.'
    if line not in script or 'SimulateAccessDenied' not in script:
        raise AssertionError('register script is missing the access denied path')
    proc = subprocess.run(
        [powershell_exe(), '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
         os.path.join(ROOT, 'register-desktop-status.ps1'), '-SimulateAccessDenied'],
        capture_output=True, text=True, encoding='utf-8', errors='replace',
    )
    if proc.returncode == 0:
        raise AssertionError('access denied simulation exited 0')
    if line not in (proc.stdout or ''):
        raise AssertionError('access denied simulation did not print the elevated PowerShell line\n' + (proc.stdout or '') + (proc.stderr or ''))


def check_overlay():
    screen = (220, 280)
    wallpaper = Image.new('RGB', screen, (80, 90, 110))
    glass = board.build_glass(screen, wallpaper)
    if glass.getpixel((1, 1))[3] != 0:
        raise AssertionError('glass corner is not transparent')
    top = glass.getpixel((110, board.GLASS_INSET + 8))
    bottom = glass.getpixel((110, screen[1] - board.GLASS_INSET - 8))
    if (top[0] + top[1] + top[2]) <= (bottom[0] + bottom[1] + bottom[2]):
        raise AssertionError(f'glass top {top} is not lighter than the bottom {bottom}')
    size = board.overlay_size((900, 640), 'on')
    rect = board.pill_rect(size, 'on')
    mid = (rect[0] + rect[2]) / 2
    if board.hit_test(rect[0] + 4, rect[1] + 4, size, 'on') != 'on':
        raise AssertionError('ON half of the pill did not take the click')
    if board.hit_test(mid + 4, rect[1] + 4, size, 'on') != 'off':
        raise AssertionError('OFF half of the pill did not take the click')
    if board.hit_test(8, 8, size, 'on') is not None:
        raise AssertionError('a click outside the pill was captured')
    folder = os.path.join(ROOT, 'fixtures', 'healthy')
    board.apply_fixture_env(folder)
    host = board.FixtureHost(folder, json.load(open(os.path.join(folder, 'board.json'), encoding='utf-8')))
    paths = board.resolve_paths()
    snap = board.load_snapshot(host, paths, {})
    sections = board.gather(snap)
    now = snap['now']
    wide = (900, 640)
    paper = Image.new('RGB', wide, (80, 90, 110))
    shown = board.compose_overlay(sections, now, 'FIXTURE-PC', 'on', wide, paper)
    hidden = board.compose_overlay([], now, 'FIXTURE-PC', 'off', wide, paper)
    if hidden.size[1] >= shown.size[1] or hidden.size != board.overlay_size(wide, 'off'):
        raise AssertionError('OFF did not collapse to the pill')
    origin = board.window_origin(wide, shown.size)
    chrome = board.build_glass(shown.size, board.crop_wallpaper(paper, origin, shown.size))
    changed = False
    for y in range(board.GLASS_INSET + 70, board.GLASS_INSET + 150):
        for x in range(board.GLASS_INSET + 20, board.GLASS_INSET + 180):
            if chrome.getpixel((x, y)) != shown.getpixel((x, y)):
                changed = True
                break
        if changed:
            break
    if not changed:
        raise AssertionError('ON did not draw the rows onto the glass')
    cache = tempfile.mkdtemp(prefix='glass-cache-')
    runtime = tempfile.mkdtemp(prefix='overlay-runtime-')
    try:
        calls = {'n': 0}

        def build():
            calls['n'] += 1
            return board.build_glass((160, 200), Image.new('RGB', (160, 200), (80, 90, 110)))

        _image, rebuilt = board.cached_glass(cache, '160x200|neutral', build)
        _again, reused = board.cached_glass(cache, '160x200|neutral', build)
        if not rebuilt or reused or calls['n'] != 1:
            raise AssertionError('second stamp did not reuse the cached glass')
        os.environ['DEG_RUNTIME_DIR'] = runtime
        live = board.resolve_paths()
        os.makedirs(live.render, exist_ok=True)
        state = {}
        sample = [('Your computer', [board.row('Processor', 'Cool, 48', 'ok')])]
        wall = Image.new('RGB', wide, (80, 90, 110))
        if not board.overlay_publish(live, sample, now, 'FIXTURE-PC', 'on', wide, wall, 'neutral', state):
            raise AssertionError('first overlay frame did not draw')
        dest = os.path.join(live.render, 'overlay.png')
        marked = os.path.getmtime(dest)
        if board.overlay_publish(live, sample, now, 'FIXTURE-PC', 'on', wide, wall, 'neutral', state):
            raise AssertionError('unchanged overlay redrew')
        if os.path.getmtime(dest) != marked:
            raise AssertionError('unchanged overlay rewrote the bitmap')
        changed_rows = [('Your computer', [board.row('Processor', 'Too hot, 92', 'bad')])]
        if not board.overlay_publish(live, changed_rows, now, 'FIXTURE-PC', 'on', wide, wall, 'neutral', state):
            raise AssertionError('changed overlay did not redraw')
    finally:
        os.environ.pop('DEG_RUNTIME_DIR', None)
        shutil.rmtree(cache)
        shutil.rmtree(runtime)
    env = os.environ.copy()
    env['DEG_RUNTIME_DIR'] = os.path.join(ART, 'overlay-board')
    env['DEG_RENDER_SIZE'] = '640x480'
    if os.path.isdir(env['DEG_RUNTIME_DIR']):
        shutil.rmtree(env['DEG_RUNTIME_DIR'])
    proc = subprocess.run(
        [sys.executable, os.path.join(ROOT, 'desktop_status.py'), '--overlay', '--fixture', os.path.join('fixtures', 'healthy')],
        cwd=ROOT, env=env, capture_output=True, text=True, encoding='utf-8', errors='replace',
    )
    if proc.returncode != 0 or 'Overlay window skipped (fixture)' not in (proc.stdout or ''):
        raise AssertionError('overlay fixture did not skip the window\n' + (proc.stdout or '') + (proc.stderr or ''))
    if 'Wallpaper set' in (proc.stdout or ''):
        raise AssertionError('overlay fixture tried to set the wallpaper')


if __name__ == '__main__':
    try:
        sys.exit(main(sys.argv[1:]))
    except Exception:
        traceback.print_exc()
        sys.exit(1)
