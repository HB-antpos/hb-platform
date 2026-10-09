import assert from "node:assert/strict";
import { readdir, readFile } from "node:fs/promises";
import { relative } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

import ts from "typescript";

const projectRoot = new URL("../", import.meta.url);
const sourceRoots = [
  new URL("app/", projectRoot),
  new URL("src/", projectRoot),
];

const keyboardAwareHosts = new Set([
  "src/features/attendance-audit/attendance-audit-screen.tsx",
  "src/features/cashier-login/cashier-login-screen.tsx",
  "src/features/device-registration/device-registration-screen.tsx",
  "src/features/device-registration/server-connection-panel.tsx",
  "src/features/held-orders/held-orders-screen.tsx",
  "src/features/installments/installment-screen.tsx",
  "src/features/local-history/local-history-screen.tsx",
  "src/features/operation-authorization/operation-authorization-modal.tsx",
  "src/features/payment-recovery/payment-recovery-screen.tsx",
  "src/features/payments/ui/payment-screen.tsx",
  "src/features/remote-history/remote-history-screen.tsx",
  "src/features/returns/return-screen.tsx",
  "src/features/sales/ui/sales-screen.tsx",
  "src/features/settings/settings-screen.tsx",
  "src/features/special-products/special-products-screen.tsx",
]);

const legacySafeInputs = new Map([
  [
    "src/features/daily-close/daily-close-screen.tsx",
    new Set(["daily-close-count-${denominationCents}"]),
  ],
  ["src/features/payments/ui/payment-screen.tsx", new Set(["payment-amount"])],
]);

test("所有系统软键盘输入都位于键盘感知滚动容器内", async () => {
  const sourceFiles = (
    await Promise.all(sourceRoots.map((root) => collectSourceFiles(root)))
  ).flat();
  const seenAwareHosts = new Set();
  const unprotectedAwareInputs = [];
  const unexpectedBaseInputs = [];
  const unsafeRawInputs = [];

  for (const fileUrl of sourceFiles) {
    const absolutePath = fileURLToPath(fileUrl);
    const sourcePath = relative(fileURLToPath(projectRoot), absolutePath);
    const sourceText = await readFile(fileUrl, "utf8");
    const sourceFile = ts.createSourceFile(
      absolutePath,
      sourceText,
      ts.ScriptTarget.Latest,
      true,
      ts.ScriptKind.TSX,
    );
    const importedTags = importedJsxTags(sourceFile);
    let hasAwareScroll = false;
    let hasAwareInput = false;

    visitJsxOpeningElements(sourceFile, (node) => {
      const tagName = node.tagName.getText(sourceFile);
      if (importedTags.awareScroll.has(tagName)) {
        hasAwareScroll = true;
        return;
      }
      if (importedTags.awareInput.has(tagName)) {
        hasAwareInput = true;
        return;
      }
      if (importedTags.baseInput.has(tagName)) {
        if (
          sourcePath === "src/ui/controls/pos-keyboard-aware-scroll-view.tsx"
        ) {
          return;
        }
        const testId = readJsxAttribute(node, "testID", sourceFile);
        const allowed = legacySafeInputs.get(sourcePath);
        if (!testId || !allowed?.has(testId)) {
          unexpectedBaseInputs.push(
            `${sourcePath}:${sourceFile.getLineAndCharacterOfPosition(node.pos).line + 1}`,
          );
          return;
        }
        if (
          testId === "payment-amount" &&
          readJsxAttribute(node, "showSoftInputOnFocus", sourceFile) !== "false"
        ) {
          unexpectedBaseInputs.push(
            `${sourcePath}:${testId}:missing-showSoftInputOnFocus=false`,
          );
        }
        return;
      }
      if (
        importedTags.nativeInput.has(tagName) &&
        sourcePath !== "src/ui/controls/pos-text-input.tsx"
      ) {
        const softInput = readJsxAttribute(
          node,
          "showSoftInputOnFocus",
          sourceFile,
        );
        if (
          sourcePath !==
            "src/core/peripherals/scanner/hid-scanner-capture.tsx" ||
          softInput !== "false"
        ) {
          unsafeRawInputs.push(
            `${sourcePath}:${sourceFile.getLineAndCharacterOfPosition(node.pos).line + 1}`,
          );
        }
      }
    });

    if (hasAwareScroll && hasAwareInput) {
      seenAwareHosts.add(sourcePath);
    }
    if (hasAwareInput) {
      unprotectedAwareInputs.push(
        ...findUnprotectedAwareInputs(sourceFile, sourcePath, importedTags),
      );
    }
  }

  assert.deepEqual(unprotectedAwareInputs, []);
  assert.deepEqual(unexpectedBaseInputs, []);
  assert.deepEqual(unsafeRawInputs, []);
  assert.deepEqual([...seenAwareHosts].sort(), [...keyboardAwareHosts].sort());
});

test("PosKeyboardAwareFlatList 与 PosKeyboardAwareScrollView 都识别为键盘感知容器", () => {
  const sourceText = `
import { PosKeyboardAwareFlatList, PosKeyboardAwareScrollView, PosKeyboardAwareTextInput } from "@/ui/controls/pos-keyboard-aware-scroll-view";

export function FlatListScreen() {
  return (
    <PosKeyboardAwareFlatList
      data={[1, 2]}
      renderItem={() => <PosKeyboardAwareTextInput testID="flat-input" />}
    />
  );
}

export function ScrollViewScreen() {
  return (
    <PosKeyboardAwareScrollView>
      <PosKeyboardAwareTextInput testID="scroll-input" />
    </PosKeyboardAwareScrollView>
  );
}
`;
  const sourceFile = ts.createSourceFile(
    "virtual-keyboard-aware-screen.tsx",
    sourceText,
    ts.ScriptTarget.Latest,
    true,
    ts.ScriptKind.TSX,
  );
  const importedTags = importedJsxTags(sourceFile);

  assert.ok(importedTags.awareScroll.has("PosKeyboardAwareFlatList"));
  assert.ok(importedTags.awareScroll.has("PosKeyboardAwareScrollView"));
  assert.deepEqual(
    findUnprotectedAwareInputs(
      sourceFile,
      "virtual-keyboard-aware-screen.tsx",
      importedTags,
    ),
    [],
  );
});

test("日结保留已验证的键盘 inset 与焦点滚动闭环", async () => {
  const dailyCloseSource = await readFile(
    new URL(
      "../src/features/daily-close/daily-close-screen.tsx",
      import.meta.url,
    ),
    "utf8",
  );

  assert.match(dailyCloseSource, /automaticallyAdjustKeyboardInsets/);
  assert.match(dailyCloseSource, /scrollResponderScrollNativeHandleToKeyboard/);
  assert.match(dailyCloseSource, /onFocus=\{revealSummaryInput\}/);
});

async function collectSourceFiles(directoryUrl) {
  const entries = await readdir(directoryUrl, { withFileTypes: true });
  const files = [];
  for (const entry of entries) {
    const entryUrl = new URL(
      `${entry.name}${entry.isDirectory() ? "/" : ""}`,
      directoryUrl,
    );
    if (entry.isDirectory()) {
      files.push(...(await collectSourceFiles(entryUrl)));
      continue;
    }
    if (
      entry.name.endsWith(".tsx") &&
      !entry.name.includes(".test.") &&
      !entry.name.includes(".rntl.")
    ) {
      files.push(entryUrl);
    }
  }
  return files;
}

function visitJsxOpeningElements(sourceFile, visitor) {
  const visit = (node) => {
    if (ts.isJsxOpeningElement(node) || ts.isJsxSelfClosingElement(node)) {
      visitor(node);
    }
    ts.forEachChild(node, visit);
  };
  visit(sourceFile);
}

function importedJsxTags(sourceFile) {
  const tags = {
    awareInput: new Set(),
    awareScroll: new Set(),
    baseInput: new Set(),
    nativeInput: new Set(),
  };

  for (const statement of sourceFile.statements) {
    if (
      !ts.isImportDeclaration(statement) ||
      !ts.isStringLiteral(statement.moduleSpecifier) ||
      !statement.importClause?.namedBindings ||
      !ts.isNamedImports(statement.importClause.namedBindings)
    ) {
      continue;
    }

    const moduleName = statement.moduleSpecifier.text;
    for (const specifier of statement.importClause.namedBindings.elements) {
      const importedName = (specifier.propertyName ?? specifier.name).text;
      const localName = specifier.name.text;
      if (
        moduleName === "@/ui/controls/pos-keyboard-aware-scroll-view" &&
        importedName === "PosKeyboardAwareScrollView"
      ) {
        tags.awareScroll.add(localName);
      } else if (
        moduleName === "@/ui/controls/pos-keyboard-aware-scroll-view" &&
        importedName === "PosKeyboardAwareFlatList"
      ) {
        tags.awareScroll.add(localName);
      } else if (
        moduleName === "@/ui/controls/pos-keyboard-aware-scroll-view" &&
        importedName === "PosKeyboardAwareTextInput"
      ) {
        tags.awareInput.add(localName);
      } else if (
        moduleName === "@/ui/controls/pos-text-input" &&
        importedName === "PosTextInput"
      ) {
        tags.baseInput.add(localName);
      } else if (
        moduleName === "react-native" &&
        importedName === "TextInput"
      ) {
        tags.nativeInput.add(localName);
      }
    }
  }

  return tags;
}

function findUnprotectedAwareInputs(sourceFile, sourcePath, importedTags) {
  const components = collectFunctionComponents(sourceFile);
  const componentNames = new Set(components.keys());
  const incomingEdges = new Map();
  const inputUsages = [];

  for (const [caller, body] of components) {
    visitJsxOpeningElements(body, (node) => {
      const tagName = node.tagName.getText(sourceFile);
      const protectedLocally = hasAwareScrollAncestor(
        node,
        body,
        sourceFile,
        importedTags.awareScroll,
      );
      if (importedTags.awareInput.has(tagName)) {
        inputUsages.push({
          component: caller,
          line: sourceFile.getLineAndCharacterOfPosition(node.pos).line + 1,
          protectedLocally,
        });
      }
      if (!componentNames.has(tagName)) return;
      const edges = incomingEdges.get(tagName) ?? [];
      edges.push({ caller, protectedLocally });
      incomingEdges.set(tagName, edges);
    });
  }

  const protectionCache = new Map();
  const isComponentAlwaysProtected = (component, visiting = new Set()) => {
    if (protectionCache.has(component)) {
      return protectionCache.get(component);
    }
    if (visiting.has(component)) return false;
    const incoming = incomingEdges.get(component) ?? [];
    if (incoming.length === 0) {
      protectionCache.set(component, false);
      return false;
    }
    const nextVisiting = new Set(visiting);
    nextVisiting.add(component);
    const protectedOnEveryPath = incoming.every(
      (edge) =>
        edge.protectedLocally ||
        isComponentAlwaysProtected(edge.caller, nextVisiting),
    );
    protectionCache.set(component, protectedOnEveryPath);
    return protectedOnEveryPath;
  };

  return inputUsages
    .filter(
      (usage) =>
        !usage.protectedLocally && !isComponentAlwaysProtected(usage.component),
    )
    .map(
      (usage) =>
        `${sourcePath}:${usage.line}:${usage.component}:missing-aware-scroll-ancestor`,
    );
}

function collectFunctionComponents(sourceFile) {
  const components = new Map();
  for (const statement of sourceFile.statements) {
    if (ts.isFunctionDeclaration(statement) && statement.name) {
      components.set(statement.name.text, statement.body);
      continue;
    }
    if (!ts.isVariableStatement(statement)) continue;
    for (const declaration of statement.declarationList.declarations) {
      if (
        !ts.isIdentifier(declaration.name) ||
        !declaration.initializer ||
        (!ts.isArrowFunction(declaration.initializer) &&
          !ts.isFunctionExpression(declaration.initializer))
      ) {
        continue;
      }
      components.set(declaration.name.text, declaration.initializer.body);
    }
  }
  return components;
}

function hasAwareScrollAncestor(
  node,
  componentBody,
  sourceFile,
  awareScrollNames,
) {
  let current = node.parent;
  while (current && current !== componentBody) {
    if (
      (ts.isJsxElement(current) &&
        awareScrollNames.has(
          current.openingElement.tagName.getText(sourceFile),
        )) ||
      (ts.isJsxSelfClosingElement(current) &&
        awareScrollNames.has(current.tagName.getText(sourceFile)))
    ) {
      return true;
    }
    current = current.parent;
  }
  return false;
}

function readJsxAttribute(node, name, sourceFile) {
  const attribute = node.attributes.properties.find(
    (property) =>
      ts.isJsxAttribute(property) && property.name.getText(sourceFile) === name,
  );
  if (!attribute || !ts.isJsxAttribute(attribute)) return null;
  if (!attribute.initializer) return "true";
  if (ts.isStringLiteral(attribute.initializer)) {
    return attribute.initializer.text;
  }
  if (!ts.isJsxExpression(attribute.initializer)) return null;
  const expression = attribute.initializer.expression;
  if (!expression) return null;
  if (
    expression.kind === ts.SyntaxKind.TrueKeyword ||
    expression.kind === ts.SyntaxKind.FalseKeyword
  ) {
    return expression.getText(sourceFile);
  }
  if (ts.isTemplateExpression(expression)) {
    return expression.getText(sourceFile).slice(1, -1);
  }
  if (ts.isNoSubstitutionTemplateLiteral(expression)) {
    return expression.text;
  }
  return expression.getText(sourceFile);
}
