import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import {
  parseSemver,
  isValidSemver,
  compareSemver,
  evaluateReleaseStatus,
  assertSameVersion,
} from '../src/lib/version.js';

test('parseSemver 解析合法版本', () => {
  assert.deepEqual(parseSemver('1.2.3'), {
    major: 1,
    minor: 2,
    patch: 3,
    prerelease: null,
    raw: '1.2.3',
  });
  assert.equal(parseSemver('1.2.3-beta.1').prerelease, 'beta.1');
  assert.equal(parseSemver('abc'), null);
  assert.equal(parseSemver('1.2'), null);
  assert.equal(parseSemver(''), null);
});

test('isValidSemver', () => {
  assert.equal(isValidSemver('1.0.0'), true);
  assert.equal(isValidSemver('1.0'), false);
  assert.equal(isValidSemver(''), false);
  assert.equal(isValidSemver(1.0), false);
  assert.equal(isValidSemver(null), false);
});

test('compareSemver', () => {
  assert.equal(compareSemver('1.0.0', '1.0.1'), -1);
  assert.equal(compareSemver('1.0.1', '1.0.0'), 1);
  assert.equal(compareSemver('1.0.0', '1.0.0'), 0);
  assert.equal(compareSemver('1.0.0', '1.0.0-beta'), 1);
  assert.equal(compareSemver('1.0.0-beta', '1.0.0'), -1);
  assert.throws(() => compareSemver('x', '1.0.0'));
});

test('evaluateReleaseStatus 版本状态', () => {
  assert.equal(
    evaluateReleaseStatus({ currentVersion: '1.0.0', latestVersion: '1.1.0', minVersion: '1.0.0' }),
    'update-available',
  );
  assert.equal(
    evaluateReleaseStatus({ currentVersion: '1.0.0', latestVersion: '1.1.0', minVersion: '1.1.0' }),
    'blocked',
  );
  assert.equal(
    evaluateReleaseStatus({ currentVersion: '1.1.0', latestVersion: '1.1.0', minVersion: '1.0.0' }),
    'latest',
  );
});

test('assertSameVersion 同版本双包', () => {
  assert.equal(assertSameVersion('1.0.0', '1.0.0'), true);
  assert.equal(assertSameVersion('1.0.0', '1.0.1'), false);
});

test('共享扩展版本升级为 1.5.0', () => {
  const pkg = JSON.parse(readFileSync(new URL('../package.json', import.meta.url), 'utf8'));
  const lock = JSON.parse(readFileSync(new URL('../package-lock.json', import.meta.url), 'utf8'));
  assert.equal(pkg.version, '1.5.0');
  assert.equal(lock.version, '1.5.0');
  assert.equal(lock.packages[''].version, '1.5.0');
});
