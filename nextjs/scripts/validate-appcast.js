#!/usr/bin/env node

const fs = require('fs');
const path = require('path');
const { parseStringPromise } = require('xml2js');

async function validateAppcast() {
  const appcastPath = path.join(__dirname, '../public/appcast.xml');

  try {
    // Read the appcast file
    const xmlContent = fs.readFileSync(appcastPath, 'utf-8');

    // Parse XML
    const result = await parseStringPromise(xmlContent);

    if (!result.rss || !result.rss.channel || !result.rss.channel[0].item) {
      console.error('❌ Invalid appcast.xml structure');
      process.exit(1);
    }

    const items = result.rss.channel[0].item;
    const buildNumbers = [];
    const versions = [];

    // Extract build numbers and versions
    for (const item of items) {
      // Validate enclosure URL domain
      const enclosure = item.enclosure && item.enclosure[0] ? item.enclosure[0] : null;
      const enclosureUrl = enclosure && enclosure.$ && enclosure.$.url ? enclosure.$.url : null;
      const title = item.title ? item.title[0] : 'unknown';
      const shortVersion = item['sparkle:shortVersionString'] ? item['sparkle:shortVersionString'][0] : null;

      if (!enclosureUrl) {
        console.error(`❌ Missing enclosure url for item with title: ${title}`);
        process.exit(1);
      }

      let hostname;
      try {
        hostname = new URL(enclosureUrl).hostname;
      } catch (e) {
        console.error(`❌ Invalid enclosure URL for version ${shortVersion || title}: ${enclosureUrl}`);
        process.exit(1);
      }

      if (hostname !== 'builds.hyperwhisper.com') {
        console.error(`❌ Enclosure URL must use builds.hyperwhisper.com. Found: ${enclosureUrl}`);
        process.exit(1);
      }

      const sparkleVersion = item['sparkle:version'] ? item['sparkle:version'][0] : null;

      if (!sparkleVersion) {
        console.error(`❌ Missing sparkle:version for item with title: ${title}`);
        process.exit(1);
      }

      const buildNumber = parseInt(sparkleVersion, 10);

      if (isNaN(buildNumber)) {
        console.error(`❌ Invalid build number (sparkle:version) for version ${shortVersion}: ${sparkleVersion}`);
        process.exit(1);
      }

      buildNumbers.push({
        version: shortVersion || title,
        build: buildNumber
      });

      versions.push(shortVersion || title);
    }

    // Check for duplicate build numbers
    const buildNumberValues = buildNumbers.map(b => b.build);
    const duplicates = buildNumberValues.filter((item, index) => buildNumberValues.indexOf(item) !== index);

    if (duplicates.length > 0) {
      console.error(`❌ Duplicate build numbers found: ${duplicates.join(', ')}`);
      console.error('\nBuild number mapping:');
      buildNumbers.forEach(item => {
        const isDuplicate = duplicates.includes(item.build);
        console.error(`  ${isDuplicate ? '⚠️ ' : '  '}Version ${item.version}: Build ${item.build}`);
      });
      process.exit(1);
    }

    // Check that build numbers are in descending order (newest first)
    let previousBuild = null;
    let isDescending = true;

    for (let i = 0; i < buildNumbers.length; i++) {
      if (previousBuild !== null && buildNumbers[i].build >= previousBuild) {
        isDescending = false;
        console.error(`❌ Build numbers are not in descending order!`);
        console.error(`   Version ${buildNumbers[i].version} (build ${buildNumbers[i].build}) should have a lower build number than the previous version (build ${previousBuild})`);
        break;
      }
      previousBuild = buildNumbers[i].build;
    }

    if (!isDescending) {
      console.error('\n📋 Current build number sequence:');
      buildNumbers.forEach((item, index) => {
        const arrow = index === 0 ? ' (newest)' : '';
        console.error(`   ${item.version}: Build ${item.build}${arrow}`);
      });
      console.error('\n💡 Build numbers should decrease as you go down the list (newest versions first)');
      process.exit(1);
    }

    // Success!
    console.log('✅ Appcast validation passed!');
    console.log('\n📋 Version history:');
    buildNumbers.forEach((item, index) => {
      const label = index === 0 ? ' (latest)' : '';
      console.log(`   ${item.version}: Build ${item.build}${label}`);
    });

    // Suggest next build number
    const highestBuild = Math.max(...buildNumberValues);
    console.log(`\n💡 Next build number should be: ${highestBuild + 1}`);

  } catch (error) {
    console.error('❌ Error validating appcast.xml:', error.message);
    process.exit(1);
  }
}

// Run validation
validateAppcast().catch(error => {
  console.error('❌ Unexpected error:', error);
  process.exit(1);
});