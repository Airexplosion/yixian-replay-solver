"""Isolated multi-core solver processes with non-destructive polling and cancellation."""
import atexit
import copy
import json
import os
import pathlib
import queue
import secrets
import subprocess
import threading
import time

import metadata_catalog


class BusyError(Exception):
    pass


class Runner:
    def __init__(self):
        command = json.loads(os.environ['YX_SOLVER_COMMAND']) if 'YX_SOLVER_COMMAND' in os.environ else [
            os.environ.get('YX_SOLVER_HOST', '/root/yx-replay/solver/host/Yx.SolverHost')]
        self.process = subprocess.Popen(command + ['--data', str(metadata_catalog.data_directory())],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
            encoding='utf-8', text=True, bufsize=1)
        self.output = queue.Queue()
        self.lock = threading.Lock()
        self.reader = threading.Thread(target=self.read, daemon=True)
        self.reader.start()
        try:
            self.info = self.receive()
            if not self.info.get('ok') or not self.info.get('ready'):
                raise RuntimeError(self.info.get('error', '求解器初始化失败'))
        except Exception:
            self.close()
            raise

    def read(self):
        try:
            for line in self.process.stdout:
                self.output.put(line)
        except (OSError, ValueError):
            pass
        finally:
            self.output.put(None)

    def receive(self):
        try:
            line = self.output.get(timeout=20)
        except queue.Empty:
            raise RuntimeError('求解器响应超时')
        if line is None:
            raise RuntimeError('求解器进程已退出')
        return json.loads(line)

    def call(self, op, **arguments):
        with self.lock:
            self.process.stdin.write(json.dumps(dict(op=op, **arguments), ensure_ascii=False) + chr(10))
            self.process.stdin.flush()
            return self.receive()

    def close(self):
        if self.process.poll() is None:
            self.process.terminate()
            try:
                self.process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait(timeout=5)
        for stream in (self.process.stdin, self.process.stdout):
            if stream:
                stream.close()
        self.reader.join(timeout=2)


class Job:
    def __init__(self, runner, request):
        self.runner = runner
        self.token = secrets.token_urlsafe(24)
        self.created = time.time()
        self.last_seen = time.monotonic()
        self.lock = threading.Lock()
        self.report = {'ok': True, 'job': self.token, 'state': 'pending', 'evaluated': 0,
                       'enumerated': 0, 'exhaustive': False, 'best': []}
        started = runner.call('start', request=request)
        if not started.get('ok'):
            raise ValueError(started.get('error', '盘面无效'))
        self.report.update({key: started[key] for key in ('threads', 'searchMode', 'cores')})
        self.thread = threading.Thread(target=self.monitor, daemon=True)

    def active(self):
        with self.lock:
            return self.report['state'] in ('pending', 'running')

    def snapshot(self):
        with self.lock:
            self.last_seen = time.monotonic()
            return copy.deepcopy(self.report)

    def cancel(self):
        with self.lock:
            self.last_seen = time.monotonic()
            if self.report['state'] not in ('pending', 'running'):
                return {'ok': True, 'job': self.token, 'state': self.report['state']}
        try:
            response = self.runner.call('cancel')
        except Exception:
            with self.lock:
                if self.report['state'] in ('done', 'cancelled', 'error'):
                    return {'ok': True, 'job': self.token, 'state': self.report['state']}
            raise
        return dict(response, job=self.token)

    def monitor(self):
        try:
            while True:
                report = self.runner.call('poll')
                if 'state' not in report:
                    raise RuntimeError(report.get('error', '求解报告无效'))
                report['job'] = self.token
                with self.lock:
                    report['cores'] = self.report['cores']
                    self.report = report
                    last_seen = self.last_seen
                if report['state'] in ('done', 'cancelled', 'error'):
                    break
                if time.monotonic() - last_seen > float(os.environ.get('YX_SOLVER_IDLE_SECONDS', '900')):
                    self.runner.call('cancel')
                time.sleep(0.5)
        except Exception:
            with self.lock:
                self.report.update(ok=False, state='error', error='求解进程不可用，请重新提交任务')
        finally:
            self.runner.close()


class Jobs:
    def __init__(self):
        self.lock = threading.Lock()
        self.jobs = {}
        self.info = None

    def capabilities(self):
        with self.lock:
            if self.info is None:
                runner = Runner()
                try:
                    self.info = runner.info
                finally:
                    runner.close()
            return dict(self.info, defaultSearchMode='exhaustive', maxConcurrentJobs=1,
                        idleTimeoutSeconds=int(os.environ.get('YX_SOLVER_IDLE_SECONDS', '900')),
                        completedRetentionSeconds=3600)

    def start(self, request):
        if not isinstance(request, dict):
            raise ValueError('请求须为盘面 JSON 对象')
        request = copy.deepcopy(request)
        request.setdefault('searchMode', 'exhaustive')
        request.setdefault('threads', 0)
        request.setdefault('first', 'both')
        request.setdefault('deduplicateResults', True)
        request.setdefault('showOrderAlternatives', True)
        for key in ('me', 'foe'):
            if key not in request or not isinstance(request[key], dict) or not isinstance(request[key].get('cards'), list):
                raise ValueError('盘面须包含 me.cards 与 foe.cards')
        with self.lock:
            self.jobs = {key: job for key, job in self.jobs.items()
                         if job.active() or time.time() - job.created < 3600}
            if any(job.active() for job in self.jobs.values()):
                raise BusyError('已有求解任务运行中，请完成或取消后再提交')
            while len(self.jobs) >= 32:
                self.jobs.pop(next(iter(self.jobs)))
            runner = Runner()
            try:
                job = Job(runner, request)
            except Exception:
                runner.close()
                raise
            self.info = runner.info
            self.jobs[job.token] = job
            initial = job.snapshot()
            job.thread.start()
            return dict(initial, poll='/api/v1/solve/' + job.token)

    def get(self, token):
        with self.lock:
            job = self.jobs.get(token)
        if job is None:
            raise KeyError('求解任务不存在或已过期')
        return job

    def stop_all(self):
        with self.lock:
            jobs = list(self.jobs.values())
        for job in jobs:
            if job.active():
                try: job.cancel()
                except Exception: pass


JOBS = Jobs()
atexit.register(JOBS.stop_all)


def decorate_report(report):
    report = copy.deepcopy(report)
    for entry in report.get('best', []):
        entry['cardDetails'] = [metadata_catalog.lookup('card', identifier) for identifier in entry['cards']]
    report['catalogVersion'] = metadata_catalog.load()['version']
    return report
