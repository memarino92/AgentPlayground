const supervisor = 'coding-supervisor';

function rule(action, effect, resource = '*') {
  return { action, resource, effect };
}

function specialist(model, description, system) {
  return {
    mode: 'subagent',
    model,
    description,
    system,
    steps: 8,
    permissions: [rule('*', 'deny'), rule('read', 'allow'), rule('glob', 'allow'), rule('grep', 'allow')],
  };
}

export function codingSupervision(enabled, model) {
  const permissions = [rule('subagent', 'deny')];
  if (!enabled) return { permissions };

  const specialists = {
    'coding-implementation': specialist(model,
      'Proposes a scoped implementation with file references and concrete edits.',
      'Investigate only the assigned part of the repository. Return a concise implementation proposal or patch in your report. Cite relevant files and call out assumptions or shared dependencies. The supervisor applies changes.'),
    'coding-validation': specialist(model,
      'Finds relevant tests and reviews a scoped change for regressions.',
      'Inspect the assigned files and tests. Report findings with file references and recommend the smallest commands that cover the requested behavior. The supervisor runs commands and decides how to address findings.'),
  };
  return {
    agent: supervisor,
    permissions,
    agents: {
      [supervisor]: {
        mode: 'primary',
        model,
        description: 'Delegates focused analysis and integrates one validated repository change.',
        permissions: Object.keys(specialists).map(name => rule('subagent', 'allow', name)),
      },
      ...specialists,
    },
    prompt: [
      'Act as the coding supervisor for this job. Break broad requests into small, independently checkable tasks with explicit boundaries and acceptance criteria.',
      'Use subagent to delegate independent investigations in parallel when they can clarify the work. Give each specialist a complete, focused brief and use at most two specialists per attempt. Keep the configured model.',
      'Work on independent steps while reports are pending. Review the reports on arrival and check proposed changes against the repository. You own all edits to the shared checkout. Integrate the useful changes, resolve overlap yourself, and run the relevant tests on the combined result.',
      'Conserve the shared request and time budgets. On continuation, inspect the saved work first and delegate only gaps that still need investigation.',
    ].join(' '),
  };
}
