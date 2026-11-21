import http from 'k6/http';
import { SharedArray } from 'k6/data';

const BASE = __ENV.BASE_URL || 'http://host.docker.internal:5080';
const MODE = __ENV.MODE || 'running';
const RATE = Number(__ENV.RATE || 100);
const STEPS = (__ENV.STEPS || '100,200,300,400,500,600,800').split(',').map(Number);
const GREEDY = 10;
const FULL_EVERY = 5;

const users = new SharedArray('users', () => JSON.parse(open(`./out/${MODE}/tokens.json`)).users);
const admins = new SharedArray('admins', () => JSON.parse(open(`./out/${MODE}/tokens.json`)).admins);
const classes = new SharedArray('classes', () => JSON.parse(open(`./out/${MODE}/data.json`)).classes);
const fullClasses = classes.filter((_, i) => i % FULL_EVERY === 0);
const waiting = users.filter((x, i) => i >= GREEDY && x.waitlist !== null);

const duration = MODE === 'calibrate' ? `${STEPS.length}m` : (__ENV.DURATION || '5m');
const vus = { preAllocatedVUs: 300, maxVUs: 3000 };

export const options = {
  scenarios: {
    main: MODE === 'calibrate'
      ? {
        executor: 'ramping-arrival-rate', exec: 'mix', timeUnit: '1s', startRate: STEPS[0], ...vus,
        stages: STEPS.flatMap(rate => [{ target: rate, duration: '10s' }, { target: rate, duration: '50s' }]),
      }
      : { executor: 'constant-arrival-rate', exec: 'mix', timeUnit: '1s', rate: RATE, duration, ...vus },
    greedy: { executor: 'constant-arrival-rate', exec: 'greedy', timeUnit: '1s', rate: GREEDY * 2, duration, preAllocatedVUs: 20, maxVUs: 200 },
  },
  summaryTrendStats: ['p(50)', 'p(95)', 'p(99)', 'count'],
};

const created = [];
const entries = [];

function pick(list) {
  return list[Math.floor(Math.random() * list.length)];
}

function mainUser() {
  return users[GREEDY + Math.floor(Math.random() * (users.length - GREEDY))];
}

function call(ep, user, token, method, url, body, key) {
  const headers = { Authorization: `Bearer ${token}` };
  if (body) headers['Content-Type'] = 'application/json';
  if (key) headers['Idempotency-Key'] = key;
  const start = Date.now();
  const res = http.request(method, BASE + url, body ? JSON.stringify(body) : null, { headers, tags: { ep }, timeout: '30s' });
  console.log(`${ep},${user},${start},${Date.now()},${res.status},${res.timings.duration}`);
  return res;
}

function key() {
  return `${__VU}-${__ITER}-${Date.now()}-${Math.random()}`;
}

export function greedy() {
  const user = users[__ITER % GREEDY];
  call('greedy', user.id, user.token, 'GET', `/enrollments/${user.enrollment}`);
}

export function mix() {
  const r = Math.random() * 100;
  if (r < 40) {
    const user = mainUser();
    call('card', user.id, user.token, 'GET', `/enrollments/${user.enrollment}`);
  } else if (r < 60) {
    const user = mainUser();
    const from = new Date();
    const to = new Date(from.getTime() + 7 * 24 * 3600 * 1000);
    call('schedule', user.id, user.token, 'GET', `/schedule?from=${from.toISOString()}&to=${to.toISOString()}&limit=20`);
  } else if (r < 75) {
    const user = mainUser();
    const res = call('enroll', user.id, user.token, 'POST', '/enrollments', { classId: pick(classes), seats: 1 + Math.floor(Math.random() * 2) }, key());
    if (res.status === 201) created.push({ user, id: res.json('id') });
  } else if (r < 80) {
    const own = created.pop();
    const user = own ? own.user : mainUser();
    call('cancel', user.id, user.token, 'POST', `/enrollments/${own ? own.id : user.enrollment}/cancel`);
  } else if (r < 85) {
    const user = mainUser();
    call('seats', user.id, user.token, 'POST', `/enrollments/${user.enrollment}/seats`, { seats: 1 + Math.floor(Math.random() * 3) });
  } else if (r < 92) {
    const user = mainUser();
    const res = call('waitlist_join', user.id, user.token, 'POST', '/enrollments/waitlist', { classId: pick(fullClasses), seats: 1 }, key());
    if (res.status === 201) entries.push({ user, id: res.json('id') });
  } else if (r < 97) {
    const own = entries.pop();
    const user = own ? own.user : pick(waiting);
    call('waitlist_leave', user.id, user.token, 'POST', `/enrollments/waitlist/${own ? own.id : user.waitlist}/cancel`);
  } else {
    const today = new Date().toISOString().slice(0, 10);
    const week = new Date(Date.now() + 7 * 24 * 3600 * 1000).toISOString().slice(0, 10);
    const admin = Math.floor(Math.random() * admins.length);
    call('analytics', -(admin + 1), admins[admin], 'GET', `/analytics/load?from=${today}&to=${week}`);
  }
}
